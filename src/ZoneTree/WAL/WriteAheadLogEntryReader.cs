using ZoneTree.Collections.TimSort;
using ZoneTree.Comparers;
using ZoneTree.Exceptions.WAL;
using ZoneTree.Logger;

namespace ZoneTree.WAL;

public static class WriteAheadLogEntryReader
{
  public delegate void LogEntryReaderDelegate<TLogEntry>(BinaryReader reader, ref TLogEntry logEntry);

  public delegate void LogEntryReaderWithLengthDelegate<TLogEntry>(BinaryReader reader, ref TLogEntry logEntry, long streamLength);

  public delegate (bool isValid, TKey key, TValue value, long opIndex) LogEntryDeserializerDelegate<TKey, TValue, TLogEntry>(in TLogEntry logEntry);

  /// <summary>
  /// Tells whether a damaged record can be what a power loss leaves of
  /// writes after the last disk sync, rather than a corruption of the log.
  /// </summary>
  /// <param name="recordPosition">The start position of the damaged record.</param>
  /// <param name="readPosition">The stream position after reading the damaged record.</param>
  public delegate bool IsUnsyncedTailDelegate(long recordPosition, long readPosition);

  public static WriteAheadLogReadLogEntriesResult<TKey, TValue> ReadLogEntries<TKey, TValue, TLogEntry>(
      ILogger logger,
      Stream stream,
      bool stopReadOnException,
      bool stopReadOnChecksumFailure,
      LogEntryReaderDelegate<TLogEntry> logEntryReader,
      LogEntryDeserializerDelegate<TKey, TValue, TLogEntry> logEntryDeserializer,
      bool sortByOpIndexes
      )
  {
    return ReadLogEntries<TKey, TValue, TLogEntry>(
        logger,
        stream,
        stopReadOnException,
        stopReadOnChecksumFailure,
        (BinaryReader reader, ref TLogEntry logEntry, long streamLength) => logEntryReader(reader, ref logEntry),
        logEntryDeserializer,
        sortByOpIndexes);
  }

  public static WriteAheadLogReadLogEntriesResult<TKey, TValue> ReadLogEntries<TKey, TValue, TLogEntry>(
      ILogger logger,
      Stream stream,
      bool stopReadOnException,
      bool stopReadOnChecksumFailure,
      LogEntryReaderWithLengthDelegate<TLogEntry> logEntryReader,
      LogEntryDeserializerDelegate<TKey, TValue, TLogEntry> logEntryDeserializer,
      bool sortByOpIndexes,
      IsUnsyncedTailDelegate isUnsyncedTail = null
      )
  {
    var result = new WriteAheadLogReadLogEntriesResult<TKey, TValue>
    {
      Success = true
    };
    stream.Seek(0, SeekOrigin.Begin);
#pragma warning disable CA2000
    var binaryReader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
#pragma warning restore CA2000
    TLogEntry entry = default;

    var keyList = new List<TKey>();
    var valuesList = new List<TValue>();
    var opIndexes = new List<long>();
    var i = 0;
    var length = stream.Length;
    long maxOpIndex = 0;
    while (true)
    {
      var logEntryPosition = stream.Position;
      try
      {
        if (stream.Position == length)
          break;
        logEntryReader(binaryReader, ref entry, length);
      }
      catch (EndOfStreamException e)
      {
        // A corrupted length can also run past the end of the log.
        if (isUnsyncedTail != null && result.Success && !isUnsyncedTail(logEntryPosition, length))
        {
          var corruption = new InvalidDataException($"ReadLogEntry failed. Index={i}", e);
          logger.LogError(corruption);
          result.Exceptions.Add(i, corruption);
          result.Success = false;
          break;
        }
        var ex = new IncompleteTailRecordFoundException(e)
        {
          FileLength = length,
          RecordPosition = logEntryPosition,
          RecordIndex = i
        };
        logger.LogWarning(ex);
        result.Exceptions.Add(i, ex);
        result.Success = false;
        break;
      }
      catch (ObjectDisposedException e)
      {
        var ex = new ObjectDisposedException($"ReadLogEntry failed. Index={i}", e);
        logger.LogError(ex);
        result.Exceptions.Add(i, ex);
        result.Success = false;
        break;
      }
      catch (IOException e)
      {
        var ex = new IOException($"ReadLogEntry failed. Index={i}", e);
        logger.LogError(ex);
        result.Exceptions.Add(i, ex);
        result.Success = false;
        if (stopReadOnException) break;
      }
      catch (Exception e)
      {
        if (StopAtUnsyncedTail(logger, stream, result, isUnsyncedTail, e, logEntryPosition, length, i))
          break;
        var ex = new InvalidOperationException($"ReadLogEntry failed. Index={i}", e);
        logger.LogError(ex);
        result.Exceptions.Add(i, ex);
        result.Success = false;
        if (stopReadOnException) break;
      }
      TKey key = default;
      TValue value = default;
      long opIndex = default;
      try
      {
        (var isValid, key, value, opIndex) = logEntryDeserializer(in entry);
        if (!isValid)
        {
          var checksumFailure = new InvalidDataException($"Checksum failed. Index={i}");
          if (StopAtUnsyncedTail(logger, stream, result, isUnsyncedTail, checksumFailure, logEntryPosition, length, i))
            break;
          if (!result.Exceptions.ContainsKey(i))
            result.Exceptions.Add(i, checksumFailure);
          result.Success = false;
          if (stopReadOnChecksumFailure) break;
        }
        else
        {
          maxOpIndex = Math.Max(opIndex, maxOpIndex);
        }
      }
      catch (Exception e)
      {
        var ex = new InvalidDataException($"Deserialization of log entry failed. Index={i}", e);
        logger.LogError(ex);
        if (!result.Exceptions.ContainsKey(i))
          result.Exceptions.Add(i, ex);
        result.Success = false;
        if (stopReadOnChecksumFailure) break;
      }

      keyList.Add(key);
      valuesList.Add(value);
      opIndexes.Add(opIndex);
      ++i;
    }
    stream.Seek(0, SeekOrigin.End);
    result.MaximumOpIndex = maxOpIndex;
    if (sortByOpIndexes)
    {
      var len = opIndexes.Count;
      var arr = Enumerable.Range(0, len).ToArray();
      TimSort<int>.Sort(arr, new LogEntryReaderComparer(opIndexes));
      var keys = new TKey[len];
      var values = new TValue[len];
      for (var k = 0; k < len; k++)
      {
        var m = arr[k];
        keys[k] = keyList[m];
        values[k] = valuesList[m];
      }
      result.Keys = keys;
      result.Values = values;
      return result;
    }
    result.Keys = keyList;
    result.Values = valuesList;
    return result;
  }

  /// <summary>
  /// Reports the first damaged record as an incomplete tail record
  /// when it can be an unsynced tail, which loading truncates.
  /// </summary>
  static bool StopAtUnsyncedTail<TKey, TValue>(
      ILogger logger,
      Stream stream,
      WriteAheadLogReadLogEntriesResult<TKey, TValue> result,
      IsUnsyncedTailDelegate isUnsyncedTail,
      Exception damage,
      long recordPosition,
      long length,
      int recordIndex)
  {
    if (isUnsyncedTail == null || !result.Success || !isUnsyncedTail(recordPosition, stream.Position))
      return false;
    var ex = new IncompleteTailRecordFoundException(damage)
    {
      FileLength = length,
      RecordPosition = recordPosition,
      RecordIndex = recordIndex
    };
    logger.LogWarning(ex);
    result.Exceptions.Add(recordIndex, ex);
    result.Success = false;
    return true;
  }

  public sealed class LogEntryReaderComparer : IRefComparer<int>
  {
    readonly IList<long> OpIndexes;

    public LogEntryReaderComparer(IList<long> opIndexes)
    {
      OpIndexes = opIndexes;
    }

    public int Compare(in int x, in int y)
    {
      var r = OpIndexes[x] - OpIndexes[y];
      if (r == 0)
        return 0;
      return r < 0 ? -1 : 1;
    }
  }
}

using ZoneTree.Exceptions;

namespace ZoneTree.Cluster;

/// <summary>A domain or column with the requested name already exists.</summary>
public sealed class SchemaObjectAlreadyExistsException(string message) : ZoneTreeException(message);

/// <summary>
/// The domain or column was dropped. Its handles, and the transactions and iterators that
/// used it, can no longer be used.
/// </summary>
public sealed class SchemaObjectDroppedException(string message) : ZoneTreeException(message);

/// <summary>
/// A domain or column exists with a different definition: another kind, domain or encoding.
/// </summary>
public sealed class SchemaConflictException(string message) : ZoneTreeException(message);

/// <summary>
/// A column's serializer or comparer is missing from the encoding registry, is registered for
/// another type, or has another version than the one the column was created with.
/// </summary>
public sealed class ColumnEncodingException(string message) : ZoneTreeException(message);

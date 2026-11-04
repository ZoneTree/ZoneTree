namespace ZoneTree.Transactional.Distributed;

/// <summary>The result of an asynchronous lookup.</summary>
/// <param name="Found">Whether the key exists in the transaction's snapshot.</param>
/// <param name="Value">The value when Found is true; otherwise ignored.</param>
public readonly record struct DistributedReadResult<TValue>(bool Found, TValue Value);

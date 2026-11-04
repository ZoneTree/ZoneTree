namespace ZoneTree.Transactional.Distributed;

/// <summary>The result of an asynchronous lookup.</summary>
/// <param name="Found">Whether the key was found.</param>
/// <param name="Value">The value when Found is true; otherwise ignored.</param>
public readonly record struct DistributedReadResult<TValue>(bool Found, TValue Value);

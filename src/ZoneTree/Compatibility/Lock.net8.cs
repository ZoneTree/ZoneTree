#if NET8_0 || NET7_0 || NET6_0
namespace System.Threading;

#pragma warning disable CS9216, CA2002

internal sealed class Lock
{
  public Scope EnterScope()
  {
    Monitor.Enter(this);
    return new Scope(this);
  }

  public readonly ref struct Scope
  {
    readonly Lock Target;

    internal Scope(Lock target)
    {
      Target = target;
    }

    public void Dispose()
    {
      Monitor.Exit(Target);
    }
  }
}

#pragma warning restore CS9216, CA2002
#endif

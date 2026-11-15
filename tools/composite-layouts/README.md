# Composite key layout generator

`Generate.cs` writes `src/ZoneTree/Composite/CompositeKeyLayout.Typed.cs`, the typed layouts from
`CompositeKeyLayout<T1>` to `CompositeKeyLayout<T1, ..., T8>`: their constructors, `Create`, `Range`,
`Between`, `BeginsWith`, the one-sided ranges, `Decode`, and `TryDecode`. The base class `CompositeKeyLayout`
is written by hand, in `CompositeKeyLayout.cs`.

To change a typed member, edit the generator and run it with the .NET 10 SDK or later, from any directory:

```sh
dotnet run tools/composite-layouts/Generate.cs
```

It writes the whole file. Then build ZoneTree and run `CompositeKeyLayoutTests`.

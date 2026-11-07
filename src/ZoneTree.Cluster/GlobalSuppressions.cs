// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "Prefer smaller code size.")]

[assembly: SuppressMessage(
    "Design",
    "CA1031: Do not catch general exception types",
    Justification = "Exceptions are logged and handled by events.")]

[assembly: SuppressMessage(
    "Naming",
    "CA1711: Identifiers should not have incorrect suffix",
    Justification = "A custom queue ends with a Queue...")]

[assembly: SuppressMessage(
    "Naming",
    "CA1707: Identifiers should not contain underscores",
    Justification = "Underscores are good for SSE2, X64 like abbrevations.")]

[assembly: SuppressMessage(
    "Security",
    "CA5394: Do not use insecure randomness",
    Justification = "Randoms are not used for security.")]

[assembly: SuppressMessage(
    "Performance",
    "CA1819: Properties should not return arrays",
    Justification = "This is not a business application and returned arrays are not cloned.")]

[assembly: SuppressMessage(
    "Design",
    "CA1000: Do not declare static members on generic types",
    Justification = "Not critical.")]

[assembly: SuppressMessage(
    "Design",
    "CA1051: Do not declare visible instance fields",
    Justification = "This is low-level library, better debug performance is preferred.")]

[assembly: SuppressMessage(
    "Design",
    "CA1028: Enum storage should be Int32",
    Justification = "Bytes are important in a database.")]

[assembly: SuppressMessage(
    "Design",
    "CA1003: Use generic event handler instances",
    Justification = "Simple event delegate with no unused argument is better.")]

[assembly: SuppressMessage(
    "Design",
    "CA1062: Validate arguments of public methods",
    Justification = "Validation comes with a cost. Performance is more important.")]

[assembly: SuppressMessage(
    "Reliability",
    "CA2007: Do not directly await a Task",
    Justification = "UI threads shall not call ZoneTree directly.")]

[assembly: SuppressMessage(
    "Design",
    "CA1065: Do not raise exceptions in unexpected locations",
    Justification = "Iterator properties throw exceptions for years.")]

[assembly: SuppressMessage(
    "Design",
    "CA1034: Nested types should not be visible",
    Justification = "Disabled for now.")]

[assembly: SuppressMessage(
    "Naming",
    "CA1716: Identifiers should not match keywords",
    Justification = "Who reserved Next() keyword?")]

[assembly: SuppressMessage(
    "Naming",
    "CA1724: Type names should not match namespaces",
    Justification = "Generic types and namespaces has no collision.")]

[assembly: SuppressMessage(
    "Usage",
    "CA2201: Do not raise reserved exception types",
    Justification = "Temporarily silenced.")]

using System.Reflection;

namespace FinTrack.Shared.Kernel.Events;

/// <summary>
/// The stable name of an integration event, for example <c>budget.deposit_added</c>. Stored
/// events are identified by this name, never by the CLR type name, so types can be renamed
/// or moved without breaking stored messages. A name is never reused; an incompatible change
/// gets a new name (for example <c>budget.deposit_added_v2</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class EventNameAttribute : Attribute
{
    /// <summary>Creates the attribute; throws <see cref="ArgumentException"/> for a name in the wrong format.</summary>
    public EventNameAttribute(string name)
    {
        if (!StableName.IsValid(name))
        {
            throw new ArgumentException($"Event name '{name}' must be lower-case segments separated by dots, for example 'budget.deposit_added'.", nameof(name));
        }

        Name = name;
    }

    /// <summary>The stable name.</summary>
    public string Name { get; }

    /// <summary>
    /// The stable name of <paramref name="eventType"/>. Throws <see cref="InvalidOperationException"/>
    /// when the type has no <see cref="EventNameAttribute"/> (a bug).
    /// </summary>
    public static string Of(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return eventType.GetCustomAttribute<EventNameAttribute>(inherit: false)?.Name
            ?? throw new InvalidOperationException($"Event type {eventType.FullName} has no [EventName] attribute.");
    }
}

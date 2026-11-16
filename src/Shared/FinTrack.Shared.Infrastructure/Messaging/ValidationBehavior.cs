using FinTrack.Shared.Kernel.Results;
using FluentResults;
using FluentValidation;
using FluentValidation.Results;
using Mediator;

namespace FinTrack.Shared.Infrastructure.Messaging;

/// <summary>
/// Validates a message with every registered <see cref="IValidator{T}"/> before its handler runs.
/// Invalid input never reaches the handler; it becomes a failed result, not an exception
/// (CLAUDE.md rule 12).
/// </summary>
/// <remarks>
/// Each failed rule becomes one <see cref="AppError"/> with code <see cref="ErrorCodes.ValidationFailed"/>
/// and the parameters <c>field</c> (camelCase path, for example <c>lines[0].amount</c>) and
/// <c>rule</c> (the validator's error code, for example <c>NotEmptyValidator</c>). The value that was
/// sent is never included: it may be an amount, and error parameters may end up in logs.
/// </remarks>
/// <typeparam name="TMessage">The command or query.</typeparam>
/// <typeparam name="TResponse"><see cref="Result"/> or <see cref="Result{TValue}"/>.</typeparam>
public sealed class ValidationBehavior<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : ResultBase<TResponse>, new()
{
    private readonly IValidator<TMessage>[] validators = [.. validators];

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (validators.Length == 0)
        {
            return await next(message, cancellationToken).ConfigureAwait(false);
        }

        var failures = new List<ValidationFailure>();
        foreach (var validator in validators)
        {
            var validation = await validator.ValidateAsync(message, cancellationToken).ConfigureAwait(false);
            failures.AddRange(validation.Errors);
        }

        if (failures.Count == 0)
        {
            return await next(message, cancellationToken).ConfigureAwait(false);
        }

        return new TResponse().WithErrors(failures.Select(ToError));
    }

    private static AppError ToError(ValidationFailure failure) =>
        AppError.Validation(
            ErrorCodes.ValidationFailed,
            ("field", FieldPath(failure.PropertyName)),
            ("rule", failure.ErrorCode));

    /// <summary><c>Lines[0].Amount</c> becomes <c>lines[0].amount</c>, matching the JSON names the client sends.</summary>
    private static string FieldPath(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}

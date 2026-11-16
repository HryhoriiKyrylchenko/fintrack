using System.Text.Json;
using System.Text.Json.Serialization;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Shared.Kernel.Json;

/// <summary>
/// Writes <see cref="Money"/> as <c>{ "amount": "1.10", "currency": "PLN" }</c>.
/// </summary>
/// <remarks>
/// Reading is strict: both properties are required, each once, nothing else is allowed, the
/// amount follows <see cref="DecimalStringConverter"/>, the currency must be registered and the
/// amount must fit its precision (<see cref="Money.Create"/>; never rounded). Messages never
/// contain the amount.
/// </remarks>
internal sealed class MoneyJsonConverter : JsonConverter<Money>
{
    private const string AmountProperty = "amount";
    private const string CurrencyProperty = "currency";

    private static readonly DecimalStringConverter AmountConverter = new();
    private static readonly CurrencyJsonConverter CurrencyConverter = new();

    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"A money value must be a JSON object, not {reader.TokenType}.");
        }

        decimal? amount = null;
        Currency? currency = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case AmountProperty when amount is null:
                    amount = AmountConverter.Read(ref reader, typeof(decimal), options);
                    break;
                case CurrencyProperty when currency is null:
                    currency = CurrencyConverter.Read(ref reader, typeof(Currency), options);
                    break;
                default:
                    throw new JsonException($"A money value has an unexpected or repeated property; only '{AmountProperty}' and '{CurrencyProperty}' are allowed, once each.");
            }
        }

        if (amount is null || currency is null)
        {
            throw new JsonException($"A money value needs both '{AmountProperty}' and '{CurrencyProperty}'.");
        }

        var money = Money.Create(amount.Value, currency);
        return money.IsSuccess
            ? money.Value
            : throw new JsonException($"A money value has more decimal places than {currency.Code} allows ({currency.DecimalPlaces}).");
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WritePropertyName(AmountProperty);
        AmountConverter.Write(writer, value.Amount, options);
        writer.WritePropertyName(CurrencyProperty);
        CurrencyConverter.Write(writer, value.Currency, options);
        writer.WriteEndObject();
    }
}

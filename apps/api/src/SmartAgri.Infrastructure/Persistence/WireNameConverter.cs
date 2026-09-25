using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartAgri.Domain;

namespace SmartAgri.Infrastructure.Persistence;

/// <summary>Stores an enum as its <see cref="WireNames{TEnum}"/> name (the frontend's
/// kebab-case string) rather than its number.</summary>
internal sealed class WireNameConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public WireNameConverter()
        : base(value => WireNames<TEnum>.ToWire(value), name => WireNames<TEnum>.Parse(name))
    {
    }
}

using HomieNano.Version4.Enums;

namespace HomieNano.Version4.Extensions
{
    public static class UnitExtensions
    {
        public static string GetString(this Unit unit)
        {
            return unit switch
            {
                Unit.None => string.Empty,
                Unit.DegreeCelsius => "°C",
                Unit.DegreeFahrenheit => "°F",
                Unit.Degree => "°",
                Unit.Liter => "L",
                Unit.Galon => "gal",
                Unit.Volts => "V",
                Unit.Watt => "W",
                Unit.Ampere => "A",
                Unit.Percent => "%",
                Unit.Meter => "m",
                Unit.Feet => "ft",
                Unit.Pascal => "Pa",
                Unit.PSI => "psi",
                Unit.CountOrAmount => "#",
                Unit.Bar => "bar",
                _ => string.Empty,
            };
        }
    }
}

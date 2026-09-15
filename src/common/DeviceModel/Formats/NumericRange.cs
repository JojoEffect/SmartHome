namespace SmartHome.DeviceModel.Formats
{
    /// <summary>
    /// The bounds an integer or float property declares: an optional minimum, an
    /// optional maximum and an optional step, both ends inclusive.
    /// </summary>
    /// <remarks>
    /// Structured rather than the raw format string it is usually written as, because a
    /// string has to be re-read by everything that wants to know -- the property that
    /// enforces it, the adapter that republishes it, the discovery mapper that turns it
    /// into another convention's min/max/step -- and those readers drift. They have
    /// already: one earlier attempt at a second adapter shipped a range reader that
    /// disagreed with the property's own in three separate ways, so a payload the
    /// property refused was advertised as acceptable. Parsing happens once, here, and
    /// every consumer reads the result.
    ///
    /// An open-ended side and a step are the parts a convention is least likely to be
    /// able to carry; a closed <c>min:max</c> with both ends present is the common
    /// denominator. An adapter whose convention cannot express a range has to refuse it
    /// rather than render an approximation of it.
    ///
    /// Immutable: nothing may narrow a property's declared range after the device has
    /// announced it.
    /// </remarks>
    public class NumericRange
    {
        private NumericRange(bool hasMinimum, double minimum, bool hasMaximum, double maximum, bool hasStep, double step)
        {
            HasMinimum = hasMinimum;
            Minimum = minimum;
            HasMaximum = hasMaximum;
            Maximum = maximum;
            HasStep = hasStep;
            Step = step;
        }

        /// <summary>Whether a lower bound was declared.</summary>
        /// <remarks>
        /// A flag and a value rather than a <c>double?</c>: nanoFramework's mscorlib
        /// carries no <c>System.Nullable</c>, so <c>double?</c> does not compile on this
        /// runtime. Same three-state meaning, spelled out.
        /// </remarks>
        public bool HasMinimum { get; }

        /// <summary>The lower bound, meaningful only when <see cref="HasMinimum"/>.</summary>
        public double Minimum { get; }

        /// <summary>Whether an upper bound was declared.</summary>
        public bool HasMaximum { get; }

        /// <summary>The upper bound, meaningful only when <see cref="HasMaximum"/>.</summary>
        public double Maximum { get; }

        /// <summary>Whether a step was declared.</summary>
        public bool HasStep { get; }

        /// <summary>The step, meaningful only when <see cref="HasStep"/>. Always greater than zero.</summary>
        public double Step { get; }

        /// <summary>
        /// A closed range, both ends inclusive -- the only shape every convention can
        /// express.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// Either bound is NaN or infinite, or the minimum is above the maximum.
        /// </exception>
        public static NumericRange Between(double minimum, double maximum)
        {
            EnsureUsableBound(minimum, "minimum");
            EnsureUsableBound(maximum, "maximum");

            // The same two checks TryParse makes before it will hand back a range, held
            // here too so that the typed route cannot build one the text route refuses.
            // A range with its ends the wrong way round contains nothing at all, so a
            // property declaring it would refuse every payload for the life of the
            // device -- the exact inversion of "a malformed declaration must not start
            // refusing a controller's otherwise valid payloads".
            if (minimum > maximum)
            {
                throw new System.ArgumentException($"A range's minimum must not be above its maximum, was {minimum}:{maximum}.");
            }

            return new(true, minimum, true, maximum, false, 0);
        }

        /// <summary>A range open at the top: <c>value &gt;= minimum</c>.</summary>
        /// <exception cref="System.ArgumentException">The bound is NaN or infinite.</exception>
        public static NumericRange AtLeast(double minimum)
        {
            EnsureUsableBound(minimum, "minimum");

            return new(true, minimum, false, 0, false, 0);
        }

        /// <summary>A range open at the bottom: <c>value &lt;= maximum</c>.</summary>
        /// <exception cref="System.ArgumentException">The bound is NaN or infinite.</exception>
        public static NumericRange AtMost(double maximum)
        {
            EnsureUsableBound(maximum, "maximum");

            return new(false, 0, true, maximum, false, 0);
        }

        /// <remarks>
        /// Neither end may be NaN or an infinity, for the reason <see cref="TryParse"/>
        /// gives: they are not values a payload can carry either, and every comparison
        /// against NaN is false, so a NaN bound would silently enforce nothing while
        /// still reading as a declared one.
        /// </remarks>
        private static void EnsureUsableBound(double bound, string which)
        {
            if (double.IsNaN(bound) || double.IsInfinity(bound))
            {
                throw new System.ArgumentException($"A range's {which} must be a finite number, was {bound}.");
            }
        }

        /// <summary>
        /// The same range with a step. Returns a new instance; this type is immutable.
        /// </summary>
        /// <exception cref="System.ArgumentException">The step is not greater than zero.</exception>
        public NumericRange WithStep(double step)
        {
            if (!(step > 0) || double.IsInfinity(step))
            {
                // Written as !(step > 0) rather than step <= 0 so that a NaN step is
                // rejected too -- every comparison against NaN is false, so "NaN <= 0"
                // would let it through. An infinity passes "> 0" and is caught
                // separately: it is a number the parser can produce and no step at all.
                throw new System.ArgumentException($"A step must be a finite number greater than zero, was {step}.");
            }

            return new NumericRange(HasMinimum, Minimum, HasMaximum, Maximum, true, step);
        }

        /// <summary>
        /// Whether a value satisfies the bounds. Both ends are inclusive, and an end
        /// that was not declared cannot be violated.
        /// </summary>
        /// <remarks>
        /// The step is deliberately not enforced. Where a convention says anything about
        /// it at all, it tends to say a consumer should *round* a value to the nearest
        /// step and then check the bounds -- which changes the value rather than refusing
        /// it, and silently moving a number a controller asked for is a different
        /// behaviour from this model's, where a payload is either applied as sent or
        /// dropped. The step is carried for adapters that publish it, and for a
        /// controller to render a slider with.
        /// </remarks>
        public bool Contains(double value)
        {
            if (HasMinimum && value < Minimum)
            {
                return false;
            }

            if (HasMaximum && value > Maximum)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads the <c>[min]:[max][:step]</c> form a device author writes.
        /// </summary>
        /// <remarks>
        /// Whitespace around each part is trimmed, so <c>"5 : 30"</c> is the range 5 to
        /// 30. A form that does not parse -- a missing colon, a non-numeric bound, a
        /// minimum above its maximum, a step of zero -- declares no range at all rather
        /// than an empty one, and this returns false. That is deliberate and long-standing:
        /// a device author's malformed declaration is not a reason to start refusing a
        /// controller's otherwise valid payloads, and the malformed text is visible in
        /// the adapter's own build-time check.
        ///
        /// <c>":"</c> parses to false as well. A convention may well accept it as its
        /// default format, but it declares neither end, which is the same as declaring
        /// nothing.
        /// </remarks>
        public static bool TryParse(string format, out NumericRange? range)
        {
            range = null;

            if (format == null || format.Length == 0)
            {
                return false;
            }

            var parts = format.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
            {
                return false;
            }

            if (!TryReadBound(parts[0], out var hasMinimum, out var minimum) ||
                !TryReadBound(parts[1], out var hasMaximum, out var maximum))
            {
                return false;
            }

            if (!hasMinimum && !hasMaximum)
            {
                return false;
            }

            if (hasMinimum && hasMaximum && minimum > maximum)
            {
                return false;
            }

            var parsed = new NumericRange(hasMinimum, minimum, hasMaximum, maximum, false, 0);

            if (parts.Length == 3)
            {
                // Unlike the bounds, a step that is present must be usable: writing a
                // third part at all is a statement that there is a step.
                // Same rule WithStep enforces, checked here so that this never throws:
                // an infinity passes "> 0" and is a number the native parser can produce.
                if (!double.TryParse(parts[2].Trim(), out var step) || !(step > 0) || double.IsInfinity(step))
                {
                    return false;
                }

                parsed = parsed.WithStep(step);
            }

            range = parsed;
            return true;
        }

        /// <summary>
        /// The <c>[min]:[max][:step]</c> shape, for logs and failure messages.
        /// </summary>
        /// <remarks>
        /// Diagnostics only -- no adapter should publish this string. Two separate
        /// reasons, either of which is enough:
        ///
        /// The bounds go through <c>double.ToString("G")</c>, and "G" is the rendering
        /// this runtime gets wrong: it prints 21.5 as "21.499999999999999", which is
        /// why <c>FloatProperty</c> publishes fixed-decimal instead. An adapter that
        /// publishes a range has to render the bounds the same deliberate way.
        ///
        /// And a convention that spells a range as <c>min:max</c> with both ends present
        /// and no step has no spelling at all for an open-ended or stepped one, which
        /// then has to be refused when the device is built rather than rendered into
        /// something a controller will misread.
        /// </remarks>
        public override string ToString()
        {
            var minimum = HasMinimum ? Minimum.ToString("G") : string.Empty;
            var maximum = HasMaximum ? Maximum.ToString("G") : string.Empty;
            var step = HasStep ? $":{Step.ToString("G")}" : string.Empty;

            return $"{minimum}:{maximum}{step}";
        }

        /// <summary>
        /// Reads one end of the range. An empty part is an open end, which is a
        /// successful read of "no bound here"; anything unparseable is a failure.
        /// </summary>
        private static bool TryReadBound(string part, out bool hasBound, out double bound)
        {
            hasBound = false;
            bound = 0;

            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                return true;
            }

            if (!double.TryParse(trimmed, out bound))
            {
                return false;
            }

            // Neither end may be NaN or an infinity: they are not values a payload can
            // carry either, so a bound of one would refuse everything or nothing at
            // random depending on which comparison ran.
            if (double.IsNaN(bound) || double.IsPositiveInfinity(bound) || double.IsNegativeInfinity(bound))
            {
                bound = 0;
                return false;
            }

            hasBound = true;
            return true;
        }
    }
}

namespace EveDeck.Services;

// Generalized version of the "how do I split my P1 stock evenly across my factory planets" problem.
// Each factory planet consumes an equal amount of EVERY input per run, so the run is bounded by the
// scarcest input. We split that scarcest input as evenly as possible across the factories, hand every
// factory the same amount of the other inputs, and report how long that load lasts.
//
// Pure and side-effect free so it is trivially unit-testable — the UI and tests both call Compute().
public static class FactoryLoadCalculator
{
    public sealed record Input(string Name, long Available);

    public sealed record FactoryShare(int FactoryIndex, long PerInputQuantity);

    public sealed record Result(
        IReadOnlyList<Input> Inputs,
        string LimitingInput,
        long LimitingAvailable,
        int FactoryCount,
        long PerFactoryLow,
        int FactoriesGettingExtra,
        double RuntimeHours,
        IReadOnlyList<FactoryShare> Shares)
    {
        // How much of EACH input to load into each factory (two tiers: the first N factories get +1).
        public long BaseQuantity => PerFactoryLow;
        public long ExtraQuantity => PerFactoryLow + 1;
    }

    // <param name="burnPerHour">Units of each input one factory consumes per hour (e.g. 240).</param>
    public static Result Compute(IReadOnlyList<Input> inputs, int factoryCount, double burnPerHour)
    {
        if (inputs is null || inputs.Count == 0)
            throw new ArgumentException("At least one input is required.", nameof(inputs));
        if (factoryCount <= 0)
            throw new ArgumentException("Factory count must be positive.", nameof(factoryCount));
        if (burnPerHour <= 0)
            throw new ArgumentException("Burn rate must be positive.", nameof(burnPerHour));

        var limiting = inputs[0];
        foreach (var i in inputs)
            if (i.Available < limiting.Available) limiting = i;

        var lowest = Math.Max(0, limiting.Available);
        var perFactoryLow = lowest / factoryCount;                 // floor
        var extraCount = (int)(lowest % factoryCount);             // this many factories get +1

        var shares = new List<FactoryShare>(factoryCount);
        for (var f = 0; f < factoryCount; f++)
            shares.Add(new FactoryShare(f, perFactoryLow + (f < extraCount ? 1 : 0)));

        // The low tier bounds when you must reload, so runtime is measured off it.
        var runtimeHours = perFactoryLow / burnPerHour;

        return new Result(
            inputs, limiting.Name, lowest, factoryCount,
            perFactoryLow, extraCount, runtimeHours, shares);
    }
}

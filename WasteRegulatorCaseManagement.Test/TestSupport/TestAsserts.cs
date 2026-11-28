namespace WasteRegulatorCaseManagement.Test.TestSupport;

internal static class TestAsserts
{
	public static T NotNull<T>(T? value) where T : class =>
		value ?? throw new InvalidOperationException($"Expected a non-null {typeof(T).Name}.");
}
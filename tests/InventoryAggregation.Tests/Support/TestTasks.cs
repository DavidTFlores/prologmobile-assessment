namespace InventoryAggregation.Tests.Support;

internal static class TestTasks
{
    /// <summary>
    /// Fails the test instead of hanging the whole run if the code under test never completes, which is
    /// what a missed fake-clock timer would otherwise look like.
    /// </summary>
    public static Task<T> OrTimeout<T>(this Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10));
}

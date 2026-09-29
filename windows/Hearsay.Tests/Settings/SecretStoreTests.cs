using Hearsay.Core.Settings;

namespace Hearsay.Tests.Settings;

/// <summary>
/// SecretStore.cs (port of mac/HearsayCore/Sources/HearsayCore/Settings/SecretStore.swift).
/// The Mac has no dedicated test file; its tests use <c>InMemorySecretStore</c>
/// through <c>AIProviderStore</c>. The in-memory store is tested fully here.
/// The Credential Manager test uses a target prefix with a fresh GUID, never
/// <c>Hearsay/</c>, so none of the owner's entries is read or touched, and
/// deletes what it wrote.
/// </summary>
public sealed class SecretStoreTests
{
    [Fact]
    public void InMemoryStoreReadsWritesAndDeletes()
    {
        Exercise(new InMemorySecretStore());
    }

    private static void Exercise(ISecretStore store)
    {
        Assert.Null(store.Read("ollama"));
        store.Write("token-1", "ollama");
        Assert.Equal("token-1", store.Read("ollama"));
        store.Write("token-2", "ollama");
        Assert.Equal("token-2", store.Read("ollama"));
        store.Write("other", "custom");
        store.Delete("ollama");
        Assert.Null(store.Read("ollama"));
        Assert.Equal("other", store.Read("custom"));
        store.Delete("ollama");
        store.Delete("never-written");
        store.Delete("custom");
        Assert.Null(store.Read("custom"));
    }

    [Fact]
    public void InMemoryStoreStartsWithGivenSecretsAndCopiesThem()
    {
        var seed = new Dictionary<string, string> { ["custom"] = "abc" };
        var store = new InMemorySecretStore(seed);
        seed["custom"] = "changed";
        Assert.Equal("abc", store.Read("custom"));
        store.Write("def", "custom");
        Assert.Equal("changed", seed["custom"]);
    }

    [Fact]
    public void InMemoryStoreIsThreadSafe()
    {
        var store = new InMemorySecretStore();
        Parallel.For(0, 1000, i =>
        {
            store.Write($"s{i}", $"a{i % 10}");
            _ = store.Read($"a{i % 7}");
            if (i % 3 == 0) store.Delete($"a{i % 5}");
        });
        for (var i = 0; i < 10; i++) _ = store.Read($"a{i}");
    }

    [Fact]
    public void TargetNameIsPrefixedWithHearsay()
    {
        var store = new CredentialManagerSecretStore();
        Assert.Equal("Hearsay/", store.TargetPrefix);
        Assert.Equal("Hearsay/custom", store.TargetName("custom"));
        Assert.Throws<ArgumentException>(() => store.TargetName(""));
    }

    [Fact]
    public void CredentialManagerRoundTripsAndDeletes()
    {
        var store = new CredentialManagerSecretStore($"HearsayTests-{Guid.NewGuid():N}/");
        const string account = "round-trip";
        try
        {
            Assert.Null(store.Read(account));
            store.Write("sk-test-é測試", account);
            Assert.Equal("sk-test-é測試", store.Read(account));
            store.Write(new string('x', 2000), account);
            Assert.Equal(new string('x', 2000), store.Read(account));
            store.Delete(account);
            Assert.Null(store.Read(account));
            store.Delete(account);

            // The same contract the in-memory store passes.
            Exercise(store);
        }
        finally
        {
            store.Delete(account);
            store.Delete("ollama");
            store.Delete("custom");
        }
        Assert.Null(store.Read(account));
        Assert.Null(store.Read("custom"));
    }

    [Fact]
    public void CredentialManagerErrorIsDescribed()
    {
        var error = new SecretStoreException(5);
        Assert.Equal(5, error.ErrorCode);
        Assert.StartsWith("Credential Manager error: ", error.Message, StringComparison.Ordinal);
        Assert.False(error.Message.EndsWith('.'));
    }
}

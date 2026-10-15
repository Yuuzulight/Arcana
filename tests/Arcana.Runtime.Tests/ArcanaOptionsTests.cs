using Arcana.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Arcana.Runtime.Tests;

[TestClass]
public sealed class ArcanaOptionsTests
{
    [TestMethod]
    public void DefaultsAreLoopbackAndBounded()
    {
        var options = new ArcanaOptions();

        options.Validate();

        Assert.AreEqual("127.0.0.1", options.Host);
        Assert.AreEqual(1, options.WorkerCount);
        Assert.AreEqual(16, options.MaxConnections);
        Assert.IsFalse(options.AllowPrivateNetwork);
    }

    [TestMethod]
    public void NonLoopbackHostIsRejected()
    {
        var options = new ArcanaOptions { Host = "0.0.0.0" };

        Assert.ThrowsException<ArgumentException>(options.Validate);
    }

    [TestMethod]
    public void InvalidResourceLimitsAreRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ArcanaOptions { WorkerCount = 0 }.Validate());
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ArcanaOptions { MaxConnections = 0 }.Validate());
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ArcanaOptions { Port = 65536 }.Validate());
    }

    [TestMethod]
    public void DefaultBrowserFallbackRejectsNonWebUrls()
    {
        Assert.ThrowsException<ArgumentException>(() => ArcanaFallback.OpenDefaultBrowser("file:///sensitive.txt"));
    }
}

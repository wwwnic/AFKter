using AFKtor.Core;
using NUnit.Framework;

namespace AFKtor.Tests;

public sealed class AcceptDelaySettingsTests
{
    private string directory = null!;
    private string SettingsPath => Path.Combine(directory, "settings.json");

    [SetUp]
    public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "AFKtor-tests-" + Guid.NewGuid());

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void FirstLaunchDefaultsToThreeSecondsWithoutCreatingAFile()
    {
        Assert.That(new AcceptDelaySettingsStore(SettingsPath).Load().DelaySeconds, Is.EqualTo(3));
        Assert.That(Directory.Exists(directory), Is.False);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(5)]
    [TestCase(30)]
    public void SavedDelaySurvivesANewStoreInstance(int seconds)
    {
        new AcceptDelaySettingsStore(SettingsPath).Save(new AcceptDelaySettings(seconds));
        Assert.That(new AcceptDelaySettingsStore(SettingsPath).Load().DelaySeconds, Is.EqualTo(seconds));
        Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        Assert.That(File.ReadAllText(SettingsPath), Does.Not.Contain("Armed").And.Not.Contain("secret"));
    }

    [TestCase(-1)]
    [TestCase(31)]
    public void InvalidDelayIsRejected(int seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new AcceptDelaySettings(seconds));

    [TestCase("not json")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("[]")]
    [TestCase("{\"DelaySeconds\":-1}")]
    [TestCase("{\"DelaySeconds\":31}")]
    [TestCase("{\"DelaySeconds\":1.5}")]
    [TestCase("{\"DelaySeconds\":\"5\"}")]
    public void InvalidStoredSettingsFallBackToThreeSeconds(string contents)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, contents);
        Assert.That(new AcceptDelaySettingsStore(SettingsPath).Load().DelaySeconds, Is.EqualTo(3));
        Assert.That(File.ReadAllText(SettingsPath), Is.EqualTo(contents));
    }

    [Test]
    public void UnreadableSettingsFallBackWithoutCrashing()
    {
        Directory.CreateDirectory(SettingsPath);
        Assert.That(new AcceptDelaySettingsStore(SettingsPath).Load().DelaySeconds, Is.EqualTo(3));
    }

    [Test]
    public void SaveFailureIsReportedAndDoesNotDamageExistingDestination()
    {
        // A directory at the destination makes replacement fail on Windows and Unix.
        Directory.CreateDirectory(SettingsPath);
        var marker = Path.Combine(SettingsPath, "existing-data");
        File.WriteAllText(marker, "keep me");
        Assert.Catch<IOException>(() => new AcceptDelaySettingsStore(SettingsPath).Save(new AcceptDelaySettings(5)));
        Assert.That(File.ReadAllText(marker), Is.EqualTo("keep me"));
        Assert.That(Directory.GetFiles(directory), Is.Empty, "Temporary files must be cleaned up.");
    }

    [Test]
    public void SavingAgainReplacesThePreviousSetting()
    {
        var store = new AcceptDelaySettingsStore(SettingsPath);
        store.Save(new AcceptDelaySettings(5));
        store.Save(new AcceptDelaySettings(1));
        Assert.That(new AcceptDelaySettingsStore(SettingsPath).Load().DelaySeconds, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
    }
}

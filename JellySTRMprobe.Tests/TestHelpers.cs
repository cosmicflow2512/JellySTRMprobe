using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Moq;

namespace JellySTRMprobe.Tests;

/// <summary>
/// Shared test helpers for creating mock objects.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Ensures Plugin.Instance is initialized for testing.
    /// </summary>
    public static void EnsurePluginInstance()
    {
        var mockPaths = new Mock<IApplicationPaths>();
        mockPaths.Setup(p => p.PluginConfigurationsPath).Returns("/tmp/jellyfin-test/plugin-configs");
        mockPaths.Setup(p => p.DataPath).Returns("/tmp/jellyfin-test/data");
        mockPaths.Setup(p => p.ConfigurationDirectoryPath).Returns("/tmp/jellyfin-test/config");
        mockPaths.Setup(p => p.PluginsPath).Returns("/tmp/jellyfin-test/plugins");
        mockPaths.Setup(p => p.ProgramDataPath).Returns("/tmp/jellyfin-test");
        mockPaths.Setup(p => p.LogDirectoryPath).Returns("/tmp/jellyfin-test/log");
        mockPaths.Setup(p => p.CachePath).Returns("/tmp/jellyfin-test/cache");

        var config = new PluginConfiguration();
        var mockSerializer = new Mock<IXmlSerializer>();
        mockSerializer
            .Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(config);

        _ = new Plugin(mockPaths.Object, mockSerializer.Object);
    }

    /// <summary>
    /// Creates a BaseItem mock with properties set directly.
    /// </summary>
    /// <param name="name">The item name.</param>
    /// <param name="path">The item path.</param>
    /// <param name="mediaStreams">The media streams to return from GetMediaStreams (defaults to an empty list).</param>
    /// <param name="runTimeTicks">The RunTimeTicks value (defaults to null).</param>
    /// <returns>The mocked item.</returns>
    public static BaseItem CreateTestItem(
        string name,
        string? path = null,
        IReadOnlyList<MediaStream>? mediaStreams = null,
        long? runTimeTicks = null)
    {
        var mock = new Mock<BaseItem>() { CallBase = true };
        mock.Setup(m => m.GetMediaStreams()).Returns((mediaStreams ?? new List<MediaStream>()).ToList());
        mock.Object.Name = name;
        mock.Object.Path = path;
        mock.Object.Id = Guid.NewGuid();
        mock.Object.RunTimeTicks = runTimeTicks;
        return mock.Object;
    }
}

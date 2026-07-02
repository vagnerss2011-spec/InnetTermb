using System.Collections.Generic;

namespace RemoteOps.Desktop.Changelog;

public interface IChangelogSource
{
    IReadOnlyList<ChangelogEntry> Load();
}

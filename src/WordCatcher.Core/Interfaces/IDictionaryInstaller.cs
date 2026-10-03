using System;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface IDictionaryInstaller
{
    Task InstallFromUrlAsync(Uri url, IProgress<DictionaryInstallProgress> progress, CancellationToken ct = default);
    Task InstallFromFileAsync(string path, IProgress<DictionaryInstallProgress> progress, CancellationToken ct = default);
    Task RemoveAsync(CancellationToken ct = default);
}

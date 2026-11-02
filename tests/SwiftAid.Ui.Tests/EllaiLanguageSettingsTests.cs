using System.Reflection;
using Microsoft.JSInterop;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Pages;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiLanguageSettingsTests
{
    [Fact]
    public async Task OverlappingSelectionsPreserveBothChoicesWhileStorageIsPending()
    {
        using var component = new EllaiSettingsAbout();
        var js = new DelayedStorage();
        typeof(EllaiSettingsAbout).GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, js);
        Task Change(string method, string language) => (Task)typeof(EllaiSettingsAbout)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, [language])!;
        var input = Change("SetInputLanguageAsync", "sna");
        var output = Change("SetOutputLanguageAsync", "ssw");
        typeof(EllaiSettingsAbout).GetField("_englishVoices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, new EllaiEnglishVoiceDto[] { new("am_michael", "Michael") });
        var voice = Change("SetEnglishVoiceAsync", "am_michael");
        var selected = (EllaiLanguagePreferences)typeof(EllaiSettingsAbout)
            .GetField("_languages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
        js.Release.TrySetResult();
        await Task.WhenAll(input, output, voice);
        Assert.Equal(new("sna", "ssw", "am_michael"), selected);
        Assert.Contains(js.Saved, value => value == new EllaiLanguagePreferences("sna", "ssw", "am_michael"));
    }

    private sealed class DelayedStorage : IJSRuntime, IJSObjectReference
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentBag<EllaiLanguagePreferences> Saved { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (identifier == "import") { await Release.Task.WaitAsync(ct); return (TValue)(object)this; }
            if (identifier == "save") { Saved.Add((EllaiLanguagePreferences)args![0]!); return (TValue)(object)true; }
            return default!;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

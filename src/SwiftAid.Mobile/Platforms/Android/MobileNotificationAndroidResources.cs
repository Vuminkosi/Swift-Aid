using Android.App;
using Android.Content;
using Android.Media;
using AndroidX.Core.App;
using Plugin.Firebase.CloudMessaging;

namespace SwiftAid.Mobile;

internal static class MobileNotificationAndroidResources
{
    public const string QuietChannelId = "swiftaid_notifications_quiet_v1";
    public const string GentleChannelId = "swiftaid_notifications_gentle_v1";

    public static void EnsureGentleChannel()
    {
        var context = Android.App.Application.Context ??
            throw new InvalidOperationException("The Android application context is unavailable.");
        FirebaseCloudMessagingImplementation.ChannelId = QuietChannelId;
        FirebaseCloudMessagingImplementation.NotificationBuilderProvider = _ =>
        {
            // Background system notifications use the channel the server sets in the
            // FCM payload. Foreground local builds only see title/body/data, so they
            // use the quiet default; the gentle channel created below exists for the
            // server-selected background path.
            var builder = new NotificationCompat.Builder(context, QuietChannelId);
            builder.SetSmallIcon(Android.Resource.Drawable.SymDefAppIcon);
            builder.SetContentTitle("SwiftAid");
            builder.SetContentText("You have an update. Open SwiftAid to review it.");
            builder.SetPriority(NotificationCompat.PriorityLow);
            builder.SetAutoCancel(true);
            builder.SetSilent(true);
            return builder;
        };

        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService) ??
            throw new InvalidOperationException("The Android notification service is unavailable.");
        var quietChannel = new NotificationChannel(
            QuietChannelId,
            "SwiftAid notifications",
            NotificationImportance.Default)
        {
            Description = "Private reminders to review your assigned work."
        };
        quietChannel.SetSound(null, null);
        manager.CreateNotificationChannel(quietChannel);

        var packageName = context.PackageName ??
            throw new InvalidOperationException("The Android package name is unavailable.");
        var gentleChannel = new NotificationChannel(
            GentleChannelId,
            "SwiftAid notifications with gentle sound",
            NotificationImportance.Default)
        {
            Description = "The same private reminders with an optional gentle sound."
        };
        var sound = Android.Net.Uri.Parse($"android.resource://{packageName}/raw/relaxing_synth_ping") ??
            throw new InvalidOperationException("The notification sound URI is unavailable.");
        var audioAttributesBuilder = new AudioAttributes.Builder();
        audioAttributesBuilder.SetUsage(AudioUsageKind.Notification);
        audioAttributesBuilder.SetContentType(AudioContentType.Sonification);
        var audioAttributes = audioAttributesBuilder.Build();
        gentleChannel.SetSound(sound, audioAttributes);
        manager.CreateNotificationChannel(gentleChannel);
    }
}

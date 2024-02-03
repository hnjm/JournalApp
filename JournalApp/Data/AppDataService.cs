﻿using MudBlazor;

namespace JournalApp;

public class AppDataService(ILogger<AppDataService> logger, IDbContextFactory<AppDbContext> dbFactory, IShare share)
{
    public async Task<bool> StartImportWizard(IDialogService dialogService, string path)
    {
        logger.LogInformation("Starting import wizard");

        // Warn if an export wasn't done in the last week.
        if (DateTimeOffset.Now > LastExportDate.AddDays(7) &&
            await dialogService.ShowCustomMessageBox(string.Empty, "It's recommended to export your data first in case there are any issues; You can do this in Settings.", yesText: "Continue anyway", cancelText: "Go back") == null)
        {
            logger.LogDebug("User didn't want to import after being warned about export");
            return false;
        }

        logger.LogInformation($"Reading file: {path}");
        var sw = Stopwatch.StartNew();

        // Attempt to read the file and its archive.
        BackupFile backup;
        try
        {
            await using (var fs = File.Open(path, FileMode.Open))
            {
                backup = await BackupFile.ReadArchive(fs);
            }

            logger.LogDebug($"Archive was read successfully after {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, $"Failed to read archive after {sw.ElapsedMilliseconds}ms");
            await dialogService.ShowCustomMessageBox(string.Empty, $"Nothing happened; Failed to read archive: {ex.Message}.", showFeedbackLink: true);
            return false;
        }

        // Warn the user of what's going to happen.
        sw.Restart();
        if (await dialogService.ShowCustomMessageBox(string.Empty,
            $"The selected backup contains {backup.Days.Count} days, {backup.Categories.Count} categories/medications, {backup.Points.Count} points, and {backup.PreferenceBackups.Count} preferences. " +
            "This will replace ALL existing data, cannot be undone, and may take a few minutes.",
            yesText: "Import data", cancelText: "Cancel") == null)
        {
            logger.LogDebug($"User declined to import data after {sw.ElapsedMilliseconds}ms");
            return false;
        }

        // Restore preferences.
        Preferences.Clear();
        foreach (var (key, value) in backup.PreferenceBackups)
        {
            Preferences.Set(key, value);
            logger.LogInformation($"Preference set: {key}");
        }

        try
        {
            sw.Restart();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                await db.Points.ExecuteDeleteAsync();
                await db.Days.ExecuteDeleteAsync();
                await db.Categories.ExecuteDeleteAsync();
                await db.SaveChangesAsync();
                logger.LogDebug($"Cleared old db sets after {sw.ElapsedMilliseconds}ms");
            }

            sw.Restart();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                await db.Days.AddRangeAsync(backup.Days);
                await db.Categories.AddRangeAsync(backup.Categories);
                await db.Points.AddRangeAsync(backup.Points);
                await db.SaveChangesAsync();
                logger.LogDebug($"Added new data after {sw.ElapsedMilliseconds}ms");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Import failed during database changes after {sw.ElapsedMilliseconds}ms");
            await dialogService.ShowCustomMessageBox(string.Empty, $"Import critically failed; Database is potentially corrupt and app may need to be reinstalled due to error: {ex}.", showFeedbackLink: true);
            return false;
        }

        LastExportDate = DateTimeOffset.Now;
        logger.LogInformation("Finished import");
        return true;
    }

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "All platforms are supported or not relevant")]
    public async Task StartExportWizard(IDialogService dialogService)
    {
        logger.LogInformation("Starting export wizard");

        logger.LogDebug("Constructing backup data");
        var filePath = Path.Combine(Path.GetTempPath(), $"backup-{DateTime.Now:yyyy-MM-dd}.journalapp");
        var sw = Stopwatch.StartNew();

        var preferenceBackups = new List<PreferenceBackup>();
        foreach (var key in new[] {
            "safety_plan",
            "mood_palette",
            "tip_click_mood_grid_day",
            "tip_add_new_category",
        })
        {
            preferenceBackups.Add(new(key, Preferences.Get(key, string.Empty)));
        }

        BackupFile backupFile;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            backupFile = new()
            {
                Days = await db.Days.Include(d => d.Points).ToListAsync(),
                Categories = await db.Categories.Include(c => c.Points).ToListAsync(),
                Points = await db.Points.ToListAsync(),
                PreferenceBackups = preferenceBackups,
            };
        }

        // Create the file and write the archive to it.
        // We don't write directly to a place the user picks because that requires the harsh WRITE_EXTERNAL_STORAGE permission.
        logger.LogDebug($"Creating {filePath} after {sw.ElapsedMilliseconds}ms");
        sw.Restart();
        try
        {
            await using (var stream = File.Create(filePath))
            {
                await backupFile.WriteArchive(stream);

                logger.LogDebug($"File created and archive written in {sw.ElapsedMilliseconds}ms");
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, $"Failed to create archive after {sw.ElapsedMilliseconds}ms");
            await dialogService.ShowCustomMessageBox(string.Empty, $"Nothing happened; Failed to create archive: {ex.Message}.", showFeedbackLink: true);
            return;
        }

        // Prompt the user to share the file.
        logger.LogDebug("Share request");
        await share.RequestAsync(new ShareFileRequest
        {
            Title = "JournalApp backup",
            File = new ShareFile(filePath)
        });

        LastExportDate = DateTimeOffset.Now;
        logger.LogInformation("Finished export");
    }

    public async Task ShowExportReminderIfDue(IDialogService dialogService)
    {
        if (LastExportDate.AddDays(90) > DateTimeOffset.Now)
            return;

        logger.LogInformation($"It's been a while since last export <{LastExportDate}>");

        // We're going to show the message so let's not bug the user again until next interval.
        LastExportDate = DateTimeOffset.Now;

        await dialogService.ShowCustomMessageBox(string.Empty, "Reminder: You haven't backed your data up in a while. To keep your data safe, select \"Export\" in Settings.");
    }

    public DateTimeOffset LastExportDate
    {
        get
        {
            var lastExportString = Preferences.Get("last_export", null);

            if (DateTimeOffset.TryParse(lastExportString, out var parsed))
            {
                return parsed;
            }
            else
            {
                // If we haven't tracked this, or it's malformed, set it to current so the user won't immediately get notified after first launch.
                LastExportDate = DateTimeOffset.Now;
                return DateTimeOffset.Now;
            }
        }
        set => Preferences.Set("last_export", value.ToString("O"));
    }
}

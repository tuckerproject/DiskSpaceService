using System.Text.Json;
using System.Text.Json.Nodes;

namespace StorageWatch.Updater;

public static class CheckpointHandoffStore
{
    public static bool TryPersistRestartIntent(
        string checkpointPath,
        bool restartUiRequested,
        bool restartServerRequested,
        Action<string> log)
    {
        if (!restartUiRequested && !restartServerRequested)
        {
            log("[DIAG] Restart intent persistence skipped because no restart flags were requested.");
            return true;
        }

        try
        {
            if (!File.Exists(checkpointPath))
            {
                log($"[WARN] Restart intent was requested but checkpoint file was not found: {checkpointPath}");
                return false;
            }

            var node = JsonNode.Parse(File.ReadAllText(checkpointPath)) as JsonObject;
            if (node == null)
            {
                log("[WARN] Restart intent was requested but checkpoint JSON was invalid.");
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (restartUiRequested)
            {
                node["restartUIRequested"] = true;
                log("[UI-RESTART] Restart requested; recorded restartUIRequested=true in checkpoint.");
            }

            if (restartServerRequested)
            {
                node["restartServerRequested"] = true;
                log("[SERVER-RESTART] Restart requested; recorded restartServerRequested=true in checkpoint.");
            }

            node["lastUpdatedAtUtc"] = now.ToString("O");
            WriteCheckpoint(checkpointPath, node);
            log($"[STEP] Persisted restart intent to checkpoint: {checkpointPath}");
            return true;
        }
        catch (Exception ex)
        {
            log($"[WARN] Failed to persist restart intent: {ex.Message}");
            return false;
        }
    }

    public static bool TryPersistAgentHandoffComplete(string checkpointPath, Action<string> log)
    {
        try
        {
            if (!File.Exists(checkpointPath))
            {
                log($"[WARN] Handoff-complete marker not persisted because checkpoint file was not found: {checkpointPath}");
                return false;
            }

            var node = JsonNode.Parse(File.ReadAllText(checkpointPath)) as JsonObject;
            if (node == null)
            {
                log("[WARN] Handoff-complete marker not persisted because checkpoint JSON was invalid.");
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            node["handoffCompletedAtUtc"] = now.ToString("O");
            node["handoffState"] = 3;
            node["lastUpdatedAtUtc"] = now.ToString("O");
            WriteCheckpoint(checkpointPath, node);
            log($"[STEP] Persisted handoff-complete marker to checkpoint: {checkpointPath}");
            return true;
        }
        catch (Exception ex)
        {
            log($"[WARN] Failed to persist handoff-complete marker: {ex.Message}");
            return false;
        }
    }

    private static void WriteCheckpoint(string checkpointPath, JsonObject node)
    {
        var output = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tempPath = checkpointPath + ".tmp";
        File.WriteAllText(tempPath, output);
        File.Move(tempPath, checkpointPath, overwrite: true);
    }
}

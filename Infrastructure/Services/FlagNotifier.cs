public sealed class FlagNotifier(NotificationDispatcher dispatcher, INotificationAgent agent, QueueStore store, ILogger<FlagNotifier> logger)
{
    private static bool IsFlagged(VideoJob job) => new[] { job }.Flagged().Any();

    public async Task NotifyAsync(VideoJob finished, ModelSettings config, Func<string, CancellationToken, Task> reanalyze, CancellationToken ct)
    {
        if (!IsFlagged(finished)) return;
        var channels = dispatcher.EnabledChannels();
        if (!config.AgentEnabled || config.Mode != "local" || !AgentPolicy.HasChoice(channels))
        {
            dispatcher.Enqueue(finished);
            return;
        }
        var passes = 0;
        try
        {
            var context = new AgentContext(finished, config, channels, CanRequestMoreFrames: true);
            var decision = AgentPolicy.Guard(await agent.DecideAsync(context, ct), context);
            if (decision.Action == AgentAction.RequestMoreFrames)
            {
                passes = 1;
                await reanalyze(finished.Id, ct);
                finished = store.Get(finished.Id);
                if (!IsFlagged(finished))
                {
                    Save(finished.Id, decision, passes, "Tidak lagi ditandai setelah analisis frame tambahan. ");
                    logger.LogInformation("Agent asked for more frames for {Job}; no longer flagged, nothing sent", finished.Id);
                    return;
                }
                context = context with { Job = finished, CanRequestMoreFrames = false };
                decision = AgentPolicy.Guard(await agent.DecideAsync(context, ct), context);
            }
            Save(finished.Id, decision, passes, "");
            if (AgentAction.IsSend(decision.Action)) dispatcher.Enqueue(finished, decision);
            else logger.LogInformation("Agent chose not to notify for {Job}: {Reason}", finished.Id, decision.Reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification agent failed for {Job}; using the default behaviour", finished.Id);
            var latest = finished;
            try { latest = store.Get(finished.Id); } catch (UserError) { }
            var error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            SaveLog(latest.Id, new AgentLog { Action = "none", FallbackUsed = true, Error = error, ExtraFramePasses = passes });
            dispatcher.Enqueue(latest);
        }
    }

    private void Save(string id, AgentDecision decision, int passes, string reasonPrefix) =>
        SaveLog(id, new AgentLog
        {
            Action = decision.Action,
            Urgency = decision.Urgency,
            Summary = decision.Summary,
            Reason = reasonPrefix + decision.Reason,
            GuardOverride = decision.GuardOverride,
            ExtraFramePasses = passes
        });

    private void SaveLog(string id, AgentLog log) =>
        store.Update(id, j => j with { Screening = (j.Screening ?? new ScreeningLog()) with { Agent = log } });
}
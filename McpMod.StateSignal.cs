using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Mcp;

/// <summary>
/// Push-style game-state notifications, so clients never re-ask on a timer.
///
/// <para><b>State version.</b> Every game event that can change what
/// <c>get_game_state</c> reports bumps <see cref="StateVersion"/>: run and room
/// transitions, every combat event, each executed or queued game action, the
/// overlay stack, the map screen, the hand's selection mode, and — as a
/// catch-all for UI that has no model event (event pages, reward buttons, shop
/// stock) — the scene tree's node_added / node_removed signals. A version only
/// ever grows, so a client that read version N and then acted can ask "wake me
/// after N" without missing a change that happened in between.</para>
///
/// <para><b>wait_for_change.</b> <c>POST {"action":"wait_for_change",
/// "since_version":N}</c> blocks until the version passes N <i>and</i> the
/// game has settled (no game action running or queued, no turn hand-off, no
/// screen transition), then returns the new state. Each wake-up is a real
/// change; between changes the request is parked on a task, not a timer.</para>
///
/// <para><b>Startup readiness.</b> Once <c>NGame.GameStartupComplete</c>
/// finishes — after the logo, the main menu load and the startup error checks,
/// the point at which loading a run no longer cancels startup — the bridge
/// reports <c>"ready": true</c> on <c>GET /</c> and writes
/// <c>bridge-ready.json</c> under the game's user data directory, which host
/// tooling waits on with a directory change notification.</para>
/// </summary>
public static partial class McpMod
{
    private const string ReadyDirName = "SpireLensMcpBridge";
    private const string ReadyFileName = "bridge-ready.json";
    private const int DefaultWaitTimeoutMs = 30_000;
    private const int MaxWaitTimeoutMs = 300_000;

    private static long _stateVersion;
    private static readonly object _signalLock = new();
    private static TaskCompletionSource<bool> _stateChanged = NewChangeSource();
    private static bool _changeTaskHandedOut;

    private static volatile bool _startupComplete;
    private static string? _readyFilePath;

    private static NOverlayStack? _signalOverlayStack;
    private static NMapScreen? _signalMapScreen;
    private static NPlayerHand? _signalPlayerHand;
    private static ActionQueueSet? _signalActionQueueSet;
    private static ActionExecutor? _signalActionExecutor;
    private static CombatStateTracker? _signalStateTracker;

    internal static long StateVersion => Interlocked.Read(ref _stateVersion);
    internal static bool StartupComplete => _startupComplete;
    internal static string? ReadyFilePath => _readyFilePath;

    private static TaskCompletionSource<bool> NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Records a state change and wakes every parked waiter.</summary>
    internal static void BumpState()
    {
        Interlocked.Increment(ref _stateVersion);
        TaskCompletionSource<bool> fired;
        lock (_signalLock)
        {
            // Node churn bumps constantly; only swap when someone is waiting.
            if (!_changeTaskHandedOut) return;
            fired = _stateChanged;
            _stateChanged = NewChangeSource();
            _changeTaskHandedOut = false;
        }
        fired.TrySetResult(true);
    }

    /// <summary>
    /// A task that completes at the next <see cref="BumpState"/>. Take it
    /// before reading <see cref="StateVersion"/> so a change between the two
    /// still wakes the caller.
    /// </summary>
    private static Task NextChange()
    {
        lock (_signalLock)
        {
            _changeTaskHandedOut = true;
            return _stateChanged.Task;
        }
    }

    // -------- Subscriptions (main thread) --------

    private static void InitializeStateSignals(SceneTree tree)
    {
        _readyFilePath = Path.Combine(OS.GetUserDataDir(), ReadyDirName, ReadyFileName);
        // A file left by an earlier process (killed, crashed) must not read as
        // this process being ready.
        try { File.Delete(_readyFilePath); } catch { }

        tree.NodeAdded += OnSignalNodeAdded;
        tree.NodeRemoved += _ => BumpState();

        AttachSingletonSignals();
        AttachRunSignals();
        AttachCombatSignals();
        AttachNode(NOverlayStack.Instance);
        AttachNode(NMapScreen.Instance);
        AttachNode(NPlayerHand.Instance);

        if (NGame.Instance != null) TrackStartup(NGame.Instance);
    }

    private static void OnSignalNodeAdded(Node node)
    {
        BumpState();
        if (node is NGame game) TrackStartup(game);
        else AttachNode(node);
    }

    private static void AttachNode(Node? node)
    {
        try
        {
            switch (node)
            {
                case NOverlayStack stack when !ReferenceEquals(stack, _signalOverlayStack):
                    stack.Changed += BumpState;
                    _signalOverlayStack = stack;
                    break;
                case NMapScreen map when !ReferenceEquals(map, _signalMapScreen):
                    map.Opened += BumpState;
                    map.Closed += BumpState;
                    _signalMapScreen = map;
                    break;
                case NPlayerHand hand when !ReferenceEquals(hand, _signalPlayerHand):
                    hand.ModeChanged += BumpState;
                    _signalPlayerHand = hand;
                    break;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireLensMcpBridge] Could not attach state signal to {node?.GetType().Name}: {ex.Message}");
        }
    }

    private static RunManager? _signalRunManager;
    private static CombatManager? _signalCombatManager;

    /// <summary>
    /// <c>RunManager</c> and <c>CombatManager</c> live for the whole process,
    /// but may not exist yet when mods initialize, so this runs again once
    /// startup completes.
    /// </summary>
    private static void AttachSingletonSignals()
    {
        try
        {
            var run = RunManager.Instance;
            if (run != null && !ReferenceEquals(run, _signalRunManager))
            {
                run.RunStarted += _ => { AttachRunSignals(); BumpState(); };
                run.RoomEntered += BumpState;
                run.RoomExited += BumpState;
                run.ActEntered += BumpState;
                _signalRunManager = run;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireLensMcpBridge] Run signals unavailable: {ex.Message}");
        }

        try
        {
            var combat = CombatManager.Instance;
            if (combat != null && !ReferenceEquals(combat, _signalCombatManager))
            {
                combat.CombatSetUp += _ => { AttachCombatSignals(); BumpState(); };
                combat.CombatBegan += _ => BumpState();
                combat.CombatEnded += _ => BumpState();
                combat.CombatWon += _ => BumpState();
                combat.CreaturesChanged += _ => BumpState();
                combat.TurnStarted += _ => BumpState();
                combat.TurnEnded += _ => BumpState();
                combat.PlayerEndedTurn += (_, _) => BumpState();
                combat.PlayerUnendedTurn += _ => BumpState();
                combat.AboutToSwitchToEnemyTurn += _ => BumpState();
                combat.PlayerActionsDisabledChanged += _ => BumpState();
                _signalCombatManager = combat;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireLensMcpBridge] Combat signals unavailable: {ex.Message}");
        }
    }

    /// <summary>The action queue and executor are recreated for every run.</summary>
    private static void AttachRunSignals()
    {
        try
        {
            var queue = RunManager.Instance.ActionQueueSet;
            if (queue != null && !ReferenceEquals(queue, _signalActionQueueSet))
            {
                queue.ActionQueueChanged += BumpState;
                queue.ActionEnqueued += _ => BumpState();
                queue.ActionResumed += _ => BumpState();
                _signalActionQueueSet = queue;
            }

            var executor = RunManager.Instance.ActionExecutor;
            if (executor != null && !ReferenceEquals(executor, _signalActionExecutor))
            {
                executor.BeforeActionExecuted += _ => BumpState();
                executor.AfterActionExecuted += _ => BumpState();
                _signalActionExecutor = executor;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireLensMcpBridge] Action signals unavailable: {ex.Message}");
        }
    }

    private static void AttachCombatSignals()
    {
        try
        {
            var tracker = CombatManager.Instance.StateTracker;
            if (tracker != null && !ReferenceEquals(tracker, _signalStateTracker))
            {
                tracker.CombatStateChanged += _ => BumpState();
                _signalStateTracker = tracker;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireLensMcpBridge] Combat state signal unavailable: {ex.Message}");
        }
    }

    private static int _startupTracked;

    private static void TrackStartup(NGame game)
    {
        if (Interlocked.Exchange(ref _startupTracked, 1) == 1) return;
        game.GameStartupComplete.ContinueWith(
            _ => RunOnMainThread(OnStartupComplete),
            TaskScheduler.Default);
    }

    private static void OnStartupComplete()
    {
        _startupComplete = true;
        AttachSingletonSignals();
        AttachRunSignals();
        AttachCombatSignals();
        WriteReadyFile();
        BumpState();
        GD.Print($"[SpireLensMcpBridge] Game startup complete; bridge ready ({_readyFilePath})");
    }

    private static void WriteReadyFile()
    {
        if (_readyFilePath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_readyFilePath)!);
            var doc = new Dictionary<string, object?>
            {
                ["pid"] = System.Environment.ProcessId,
                ["port"] = _port,
                ["version"] = Version,
                ["ready_at_utc"] = DateTime.UtcNow.ToString("o"),
            };
            string tmp = _readyFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, _jsonOptions));
            File.Move(tmp, _readyFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireLensMcpBridge] Could not write {_readyFilePath}: {ex.Message}");
        }
    }

    // -------- Settledness (main thread) --------

    /// <summary>
    /// Whether the game is between steps: nothing is animating a game action,
    /// no turn is changing hands, and no screen is fading. A pending player
    /// choice (hand or grid selection, rewards) counts as settled even though
    /// the action that asked for it stays queued until the choice resumes it.
    /// </summary>
    private static (bool Settled, string? Reason) EvaluateSettled()
    {
        if (!_startupComplete) return (false, "game startup not complete");
        if (_currentRunLoadInProgress) return (false, "run save loading");
        if (NGame.Instance?.Transition?.InTransition == true) return (false, "screen transition");

        var run = RunManager.Instance;
        if (!run.IsInProgress) return (true, null);
        if (run.IsCleaningUp) return (false, "run cleaning up");

        if (AwaitingPlayerChoice()) return (true, null);

        if (NMapScreen.Instance is { IsTraveling: true }) return (false, "map travel");
        if (run.ActionExecutor?.IsRunning == true) return (false, "game action running");
        if (run.ActionQueueSet is { IsEmpty: false }) return (false, "game actions queued");

        var combat = CombatManager.Instance;
        if (combat.IsInProgress)
        {
            if (combat.IsStarting) return (false, "combat starting");
            if (combat.IsOverOrEnding) return (false, "combat ending");
            if (combat.IsEnemyTurnStarted || combat.EndingPlayerTurnPhaseOne || combat.EndingPlayerTurnPhaseTwo)
                return (false, "turn changing hands");
            if (combat.PlayerActionsDisabled) return (false, "player actions disabled");
            if (NPlayerHand.Instance == null) return (false, "combat UI not ready");
        }
        else if (run.DebugOnlyGetState()?.CurrentRoom is CombatRoom
                 && NMapScreen.Instance is not { IsOpen: true }
                 && NOverlayStack.Instance?.Peek() == null)
        {
            return (false, "combat over, rewards not shown yet");
        }

        return (true, null);
    }

    private static bool AwaitingPlayerChoice()
    {
        if (NPlayerHand.Instance is { IsInCardSelection: true }) return true;
        var top = NOverlayStack.Instance?.Peek();
        return top is NCardGridSelectionScreen
            or NChooseACardSelectionScreen
            or NChooseABundleSelectionScreen
            or IOverlayScreen;
    }

    /// <summary>Builds the state with the version read <i>before</i> building.</summary>
    private static Dictionary<string, object?> BuildVersionedState()
    {
        long version = StateVersion;
        var state = BuildGameState();
        var (settled, reason) = EvaluateSettled();
        state["state_version"] = version;
        state["settled"] = settled;
        if (!settled) state["unsettled_reason"] = reason;
        return state;
    }

    // -------- wait_for_change (HTTP thread) --------

    private static void HandleWaitForChange(Dictionary<string, JsonElement> parsed, HttpListenerResponse response)
    {
        long since = parsed.TryGetValue("since_version", out var s) && s.TryGetInt64(out var sv) ? sv : -1;
        int timeoutMs = parsed.TryGetValue("timeout_ms", out var t) && t.TryGetInt32(out var tv)
            ? Math.Clamp(tv, 0, MaxWaitTimeoutMs)
            : DefaultWaitTimeoutMs;
        bool requireSettled = !parsed.TryGetValue("require_settled", out var r)
                              || r.ValueKind != JsonValueKind.False;

        var clock = Stopwatch.StartNew();
        Dictionary<string, object?>? state = null;
        try
        {
            while (true)
            {
                Task change = NextChange();
                if (StateVersion > since)
                {
                    state = RunOnMainThread(BuildVersionedState).GetAwaiter().GetResult();
                    if (!requireSettled || state["settled"] is true)
                    {
                        SendJson(response, new Dictionary<string, object?>
                        {
                            ["status"] = "ok",
                            ["changed"] = true,
                            ["waited_ms"] = clock.ElapsedMilliseconds,
                            ["state"] = state,
                        });
                        return;
                    }
                }

                long remaining = timeoutMs - clock.ElapsedMilliseconds;
                if (remaining <= 0) break;
                change.Wait(TimeSpan.FromMilliseconds(remaining));
            }

            state ??= RunOnMainThread(BuildVersionedState).GetAwaiter().GetResult();
            SendJson(response, new Dictionary<string, object?>
            {
                ["status"] = "timeout",
                ["changed"] = StateVersion > since,
                ["waited_ms"] = clock.ElapsedMilliseconds,
                ["state"] = state,
            });
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"wait_for_change failed: {ex.Message}");
        }
    }
}

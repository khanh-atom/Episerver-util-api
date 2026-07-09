using System.Text.Json;
using EPiServer.DataAbstraction;
using EPiServer.Scheduler;
using EPiServer.ServiceLocation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Optimizely.ContentGraph.Cms.Configuration;
using Optimizely.ContentGraph.Cms.Core.Internal;
using Optimizely.ContentGraph.Cms.NetCore.Core.Internal;
using Optimizely.ContentGraph.Cms.NetCore.Models.Internal;
using Optimizely.ContentGraph.Core;
using static Optimizely.ContentGraph.Cms.NetCore.Models.Internal.SmoothDeployJobState;

namespace Foundation.Custom.Episerver_util_api.ContentGraph
{
    /// <summary>
    /// API controller to replicate and debug the Smooth Rebuild (Blue-Green deployment) flow.
    /// Each step corresponds to a separate API endpoint for easy browser testing.
    /// Base route: util-api/custom-smooth-rebuild
    /// </summary>
    [ApiController]
    [Route("util-api/custom-smooth-rebuild")]
    public class CustomSmoothRebuildController : ControllerBase
    {
        private readonly ISmoothDeployJobStore _store;
        private readonly IScheduledJobRepository _scheduledJobRepository;
        private readonly IScheduledJobExecutor _scheduledJobExecutor;
        private readonly IClient _client;
        private readonly IOptions<QueryOptions> _queryOptions;
        private readonly ILanguagesResolver _languagesResolver;

        private const string SmoothDeployJobId = "1EAC9A34-F89C-44B4-865F-BCBF7B2ABA95";
        private const string ContentIndexingJobName = "ContentIndexingJob";

        public CustomSmoothRebuildController(
            ISmoothDeployJobStore store,
            IScheduledJobRepository scheduledJobRepository,
            IScheduledJobExecutor scheduledJobExecutor,
            IClient client,
            IOptions<QueryOptions> queryOptions,
            ILanguagesResolver languagesResolver)
        {
            _store = store;
            _scheduledJobRepository = scheduledJobRepository;
            _scheduledJobExecutor = scheduledJobExecutor;
            _client = client;
            _queryOptions = queryOptions;
            _languagesResolver = languagesResolver;
        }

        /// <summary>
        /// Step 0: Shows current smooth rebuild state, prerequisites check, and all available endpoints.
        /// Sample usage: https://localhost:5009/util-api/custom-smooth-rebuild
        /// </summary>
        [HttpGet("")]
        public IActionResult Index()
        {
            try
            {
                var state = _store.LoadJobState();
                var syncJob = _scheduledJobRepository.List()
                    .FirstOrDefault(x => x.TypeName.EndsWith(ContentIndexingJobName, StringComparison.Ordinal));
                var smoothJob = _scheduledJobRepository.Get(Guid.Parse(SmoothDeployJobId));
                var languages = _languagesResolver.GetUsedLanguages()?.ToList() ?? new List<string>();

                return Ok(new
                {
                    Description = "Smooth Rebuild (Blue-Green Deployment) — Debug & Replication Controller",
                    CurrentState = new
                    {
                        Phase = state.JobPhaseReadable,
                        PhaseNumeric = (int)state.JobPhase,
                        HasActiveSlot = state.HasActiveSlot,
                        state.StartedSyncAt,
                        state.StartedDeltaSyncAt,
                        state.CommittedAt,
                        state.AbandonedAt,
                        state.RevertedAt,
                        state.StoppedAt,
                        state.ResetAt,
                        state.ItemsToSync,
                        state.ItemsSynced,
                        state.LastProcessedActivityId,
                        state.StartedBy,
                        state.StoppedBy,
                        state.CommitedBy,
                        state.AbandonedBy,
                        state.RevertedBy,
                        state.ResetBy,
                        state.LastError
                    },
                    Prerequisites = new
                    {
                        ContentSyncJobExists = syncJob != null,
                        ContentSyncJobLastExecution = syncJob?.LastExecution,
                        ContentSyncJobHasRun = syncJob != null && syncJob.LastExecution != DateTime.MinValue,
                        ResetAt = _store.LoadResettedAt(),
                        SyncJobRanAfterReset = syncJob != null && syncJob.LastExecution > _store.LoadResettedAt(),
                        SmoothDeployJobExists = smoothJob != null,
                        SmoothDeployJobLastExecution = smoothJob?.LastExecution,
                        ConfiguredLanguages = languages,
                        GatewayAddress = _queryOptions.Value.GatewayAddress,
                        HasAppKey = !string.IsNullOrWhiteSpace(_queryOptions.Value.AppKey),
                        HasSecret = !string.IsNullOrWhiteSpace(_queryOptions.Value.Secret),
                    },
                    Steps = new[]
                    {
                        "Step 0: GET /util-api/custom-smooth-rebuild — This page (state + prerequisites)",
                        "Step 1: GET /util-api/custom-smooth-rebuild/check-prerequisites — Verify all prerequisites for smooth rebuild",
                        "Step 2: GET /util-api/custom-smooth-rebuild/deploy-new-slot — Call deployNewAccountVersion mutation (creates the 'Green' slot)",
                        "Step 3: GET /util-api/custom-smooth-rebuild/get-state — Get current job state from the DDS store",
                        "Step 4: GET /util-api/custom-smooth-rebuild/set-phase?phase=SYNCINPROGRESS — Manually set the job phase (simulates UI Start button)",
                        "Step 5: GET /util-api/custom-smooth-rebuild/accept — Accept new deployment (swap Blue/Green slots)",
                        "Step 6: GET /util-api/custom-smooth-rebuild/revert — Revert to old deployment",
                        "Step 7: GET /util-api/custom-smooth-rebuild/abandon — Abandon new slot and revert delta to old",
                        "Step 8: GET /util-api/custom-smooth-rebuild/reset-to-idle — Reset state to IDLE (cleanup)",
                    },
                    SampleUrls = new[]
                    {
                        "https://localhost:5009/util-api/custom-smooth-rebuild",
                        "https://localhost:5009/util-api/custom-smooth-rebuild/check-prerequisites",
                        "https://localhost:5009/util-api/custom-smooth-rebuild/deploy-new-slot",
                        "https://localhost:5009/util-api/custom-smooth-rebuild/get-state",
                        "https://localhost:5009/util-api/custom-smooth-rebuild/set-phase?phase=SYNCINPROGRESS",
                        "https://localhost:5009/util-api/custom-smooth-rebuild/accept",
                        "https://localhost:5009/util-api/custom-smooth-rebuild/revert",
                        "https://localhost:5009/util-api/custom-smooth-rebuild/abandon",
                        "https://localhost:5009/util-api/custom-smooth-rebuild/reset-to-idle",
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 1: Verify all prerequisites required before a smooth rebuild can start.
        /// Replicates the checks in SmoothDeployJob.Execute() lines 111-126.
        /// Sample usage: https://localhost:5009/util-api/custom-smooth-rebuild/check-prerequisites
        /// </summary>
        [HttpGet("check-prerequisites")]
        public IActionResult CheckPrerequisites()
        {
            try
            {
                var state = _store.LoadJobState();
                var syncJob = _scheduledJobRepository.List()
                    .FirstOrDefault(x => x.TypeName.EndsWith(ContentIndexingJobName, StringComparison.Ordinal));
                var resetAt = _store.LoadResettedAt();
                var languages = _languagesResolver.GetUsedLanguages()?.ToList() ?? new List<string>();

                var checks = new List<object>();
                var allPassed = true;

                // Check 1: Content sync job exists
                var syncJobExists = syncJob != null;
                checks.Add(new
                {
                    Check = "ContentIndexingJob exists in scheduler",
                    Passed = syncJobExists,
                    Detail = syncJobExists
                        ? $"Found: {syncJob.TypeName}"
                        : "NOT FOUND — The 'Optimizely Graph content synchronization job' must exist",
                    CodeRef = "SmoothDeployJob.cs line 112: GetJob(nameof(ContentIndexingJob))"
                });
                if (!syncJobExists) allPassed = false;

                // Check 2: Content sync job has run at least once
                var syncJobHasRun = syncJob != null && syncJob.LastExecution != DateTime.MinValue;
                checks.Add(new
                {
                    Check = "ContentIndexingJob has been executed at least once",
                    Passed = syncJobHasRun,
                    Detail = syncJobHasRun
                        ? $"Last execution: {syncJob.LastExecution:O}"
                        : "FAILED — Run 'Optimizely Graph content synchronization job' first. The Smooth Rebuild job returns: 'Execute Optimizely Graph content synchronization job first!'",
                    CodeRef = "SmoothDeployJob.cs line 114: syncJob.LastExecution == DateTime.MinValue"
                });
                if (!syncJobHasRun) allPassed = false;

                // Check 3: Content sync job ran AFTER last reset
                var ranAfterReset = syncJob != null && syncJob.LastExecution > resetAt;
                checks.Add(new
                {
                    Check = "ContentIndexingJob ran after the last account reset",
                    Passed = ranAfterReset,
                    Detail = ranAfterReset
                        ? $"Last sync: {syncJob?.LastExecution:O} > Last reset: {resetAt:O}"
                        : $"FAILED — Last sync: {syncJob?.LastExecution:O}, Last reset: {resetAt:O}. The job returns: 'Execute Optimizely Graph content synchronization job after reset!'",
                    CodeRef = "SmoothDeployJob.cs line 121: syncJob.LastExecution < _store.LoadResettedAt()"
                });
                if (!ranAfterReset) allPassed = false;

                // Check 4: Current phase
                checks.Add(new
                {
                    Check = "Current job phase is not IDLE (job needs to be initiated from UI)",
                    Passed = state.JobPhase != SmoothDeployJobPhases.IDLE,
                    Detail = state.JobPhase == SmoothDeployJobPhases.IDLE
                        ? $"Phase is IDLE — The job will return empty string and do nothing! You must initiate from Settings > Optimizely Graph > Smooth Rebuild, or call /set-phase?phase=SYNCINPROGRESS"
                        : $"Phase is {state.JobPhaseReadable} — job will process this phase",
                    CodeRef = "SmoothDeployJob.cs line 130-132: case IDLE: return string.Empty"
                });

                // Check 5: Languages configured
                checks.Add(new
                {
                    Check = "Languages are configured for the Graph account",
                    Passed = languages.Any(),
                    Detail = languages.Any()
                        ? $"Languages: {string.Join(", ", languages)}"
                        : "No languages found — the deployNewAccountVersion mutation requires language parameters",
                    CodeRef = "GraphQLService.cs line 33: GetMutationLanguageParams()"
                });
                if (!languages.Any()) allPassed = false;

                // Check 6: Not a Developer Index
                var gatewayAddress = _queryOptions.Value.GatewayAddress ?? "";
                var isDeveloperIndex = gatewayAddress.Contains("localhost") || gatewayAddress.Contains("127.0.0.1");
                checks.Add(new
                {
                    Check = "Not using a Developer Index (local gateway)",
                    Passed = !isDeveloperIndex,
                    Detail = isDeveloperIndex
                        ? $"WARNING — Gateway '{gatewayAddress}' appears to be a Developer Index. Smooth Rebuild (Blue-Green) requires DXP cloud infrastructure."
                        : $"Gateway: {gatewayAddress}",
                    CodeRef = "Smooth Rebuild requires slot creation on server-side infrastructure"
                });
                if (isDeveloperIndex) allPassed = false;

                return Ok(new
                {
                    Step = "1 — Check Prerequisites",
                    AllPassed = allPassed,
                    Checks = checks,
                    Summary = allPassed
                        ? "All prerequisites passed. You can proceed with /deploy-new-slot or /set-phase?phase=SYNCINPROGRESS"
                        : "Some prerequisites FAILED. Fix these before attempting a smooth rebuild.",
                    NextStep = allPassed
                        ? "https://localhost:5009/util-api/custom-smooth-rebuild/set-phase?phase=SYNCINPROGRESS"
                        : null
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Call the deployNewAccountVersion GraphQL mutation to create a new 'Green' slot.
        /// This replicates what SmoothDeployJob does at line 138 when phase is SYNCINPROGRESS.
        /// Sample usage: https://localhost:5009/util-api/custom-smooth-rebuild/deploy-new-slot
        /// </summary>
        [HttpGet("deploy-new-slot")]
        public async Task<IActionResult> DeployNewSlot()
        {
            try
            {
                var languages = _languagesResolver.GetUsedLanguages()?.ToList() ?? new List<string>();
                var languageParams = string.Join(",", languages.Select(lang => $"\"{lang}\""));

                var query = $@"mutation {{ deployNewAccountVersion(languages: [{languageParams}]) }}";
                var queryStringParams = new Dictionary<string, string> { { "slot", "new" } };

                var response = await _client.QueryAsync(
                    query,
                    new { },
                    customHeaders: new Dictionary<string, string>(),
                    queryStringParams: queryStringParams);

                var containsDone = response?.Contains("\"Done\"") ?? false;

                return Ok(new
                {
                    Step = "2 — Deploy New Slot (deployNewAccountVersion mutation)",
                    Success = containsDone,
                    GraphQLMutation = query,
                    QueryStringParams = queryStringParams,
                    Languages = languages,
                    RawResponse = TryParseJson(response),
                    Explanation = containsDone
                        ? "New slot created successfully. The 'Green' slot is now ready to receive content."
                        : "Deployment FAILED. The response did not contain '\"Done\"'. Check if another operation is in progress (409 Conflict) or if the environment supports slot creation.",
                    CodeRef = "SmoothDeployJob.cs line 138-144, GraphQLService.cs line 29-42",
                    NextStep = containsDone
                        ? "https://localhost:5009/util-api/custom-smooth-rebuild/get-state"
                        : null
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Get the current SmoothDeployJobState from the DDS store.
        /// Useful to inspect the state at any point during the flow.
        /// Sample usage: https://localhost:5009/util-api/custom-smooth-rebuild/get-state
        /// </summary>
        [HttpGet("get-state")]
        public IActionResult GetState()
        {
            try
            {
                var state = _store.LoadJobState();

                return Ok(new
                {
                    Step = "3 — Get Current State",
                    State = new
                    {
                        Phase = state.JobPhaseReadable,
                        PhaseNumeric = (int)state.JobPhase,
                        HasActiveSlot = state.HasActiveSlot,
                        state.StartedSyncAt,
                        state.StartedDeltaSyncAt,
                        state.CommittedAt,
                        state.AbandonedAt,
                        state.RevertedAt,
                        state.StoppedAt,
                        state.ResetAt,
                        state.ItemsToSync,
                        state.ItemsSynced,
                        state.LastProcessedActivityId,
                        state.StartedBy,
                        state.StoppedBy,
                        state.CommitedBy,
                        state.AbandonedBy,
                        state.RevertedBy,
                        state.ResetBy,
                        state.LastError
                    },
                    PhaseExplanation = state.JobPhase switch
                    {
                        SmoothDeployJobPhases.IDLE => "Job is idle. Will return empty string if executed. Must be set to SYNCINPROGRESS to start.",
                        SmoothDeployJobPhases.SYNCINPROGRESS => "Job will create a new slot and perform full sync to the Green slot.",
                        SmoothDeployJobPhases.SYNCDELTA => "Full sync complete. Delta sync is active. You can Accept or Abandon the new slot.",
                        SmoothDeployJobPhases.ERROR => "Job encountered an error. Must be restarted.",
                        SmoothDeployJobPhases.COMMITED => "New slot has been accepted and swapped to live.",
                        SmoothDeployJobPhases.ABANDONED => "New slot was abandoned. Job will revert delta changes to old slot.",
                        _ => "Unknown phase"
                    },
                    CodeRef = "SmoothDeployJobState.cs — Stored in EPiServer DDS (Dynamic Data Store)"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Manually set the job phase. This simulates what the UI does when clicking Start/Stop.
        /// Valid phases: IDLE, SYNCINPROGRESS, SYNCDELTA, ERROR, COMMITED, ABANDONED
        /// Sample usage: https://localhost:5009/util-api/custom-smooth-rebuild/set-phase?phase=SYNCINPROGRESS
        /// </summary>
        [HttpGet("set-phase")]
        public IActionResult SetPhase([FromQuery] string phase = "SYNCINPROGRESS")
        {
            try
            {
                if (!Enum.TryParse<SmoothDeployJobPhases>(phase, ignoreCase: true, out var targetPhase))
                {
                    return Ok(new
                    {
                        Error = $"Invalid phase: '{phase}'",
                        ValidPhases = Enum.GetNames<SmoothDeployJobPhases>()
                    });
                }

                var previousState = _store.LoadJobState();
                var previousPhase = previousState.JobPhaseReadable;

                var state = _store.LoadJobState();
                state.JobPhase = targetPhase;

                if (targetPhase == SmoothDeployJobPhases.SYNCINPROGRESS)
                {
                    state.StartedSyncAt = DateTime.UtcNow;
                    state.StartedBy = "util-api-debug";
                }

                _store.SaveJobState(state);

                return Ok(new
                {
                    Step = "4 — Set Phase",
                    PreviousPhase = previousPhase,
                    NewPhase = state.JobPhaseReadable,
                    Explanation = targetPhase switch
                    {
                        SmoothDeployJobPhases.SYNCINPROGRESS => "Phase set to SYNCINPROGRESS. This is what the UI Start button does (SmoothRebuildController.cs line 84-91). Now when the scheduled job runs, it will execute the rebuild.",
                        SmoothDeployJobPhases.IDLE => "Phase reset to IDLE. The scheduled job will do nothing.",
                        _ => $"Phase set to {phase}."
                    },
                    CodeRef = "SmoothRebuildController.cs line 80-96: Start() sets SYNCINPROGRESS then calls _scheduledJobExecutor.StartAsync(job)",
                    NextStep = targetPhase == SmoothDeployJobPhases.SYNCINPROGRESS
                        ? "Now run the 'Optimizely Graph Smooth Rebuild' scheduled job from CMS admin, or call /deploy-new-slot to test the mutation directly"
                        : null
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Accept the new deployment — calls useNewAccountVersion mutation to swap Blue/Green slots.
        /// Only works when phase is SYNCDELTA (after full sync completes).
        /// Sample usage: https://localhost:5009/util-api/custom-smooth-rebuild/accept
        /// </summary>
        [HttpGet("accept")]
        public async Task<IActionResult> Accept()
        {
            try
            {
                var state = _store.LoadJobState();

                if (state.JobPhase != SmoothDeployJobPhases.SYNCDELTA)
                {
                    return Ok(new
                    {
                        Step = "5 — Accept New Deployment",
                        Error = $"Cannot accept: current phase is {state.JobPhaseReadable}, must be SYNCDELTA",
                        CurrentPhase = state.JobPhaseReadable,
                        Hint = "The Accept button is only available after the full sync completes and the phase transitions to SYNCDELTA",
                        CodeRef = "SmoothRebuildController.cs line 130: if (state.JobPhase == SmoothDeployJobPhases.SYNCDELTA)"
                    });
                }

                var languages = _languagesResolver.GetUsedLanguages()?.ToList() ?? new List<string>();
                var languageParams = string.Join(",", languages.Select(lang => $"\"{lang}\""));
                var query = $@"mutation {{ useNewAccountVersion(languages: [{languageParams}]) }}";
                var customHeaders = new Dictionary<string, string> { { "cg-use-new", "true" } };

                var response = await _client.QueryAsync(query, new { }, customHeaders: customHeaders);

                state.CommitedBy = "util-api-debug";
                state.CommittedAt = DateTime.UtcNow;
                state.JobPhase = SmoothDeployJobPhases.COMMITED;
                _store.SaveJobState(state);

                return Ok(new
                {
                    Step = "5 — Accept New Deployment (useNewAccountVersion mutation)",
                    Success = true,
                    GraphQLMutation = query,
                    CustomHeaders = customHeaders,
                    RawResponse = TryParseJson(response),
                    NewPhase = state.JobPhaseReadable,
                    Explanation = "The Green slot is now live. The old Blue slot has been retired.",
                    CodeRef = "SmoothRebuildController.cs line 124-140, GraphQLService.cs line 44-56"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Revert the deployment — calls useOldAccountVersion mutation to switch back.
        /// Sample usage: https://localhost:5009/util-api/custom-smooth-rebuild/revert
        /// </summary>
        [HttpGet("revert")]
        public async Task<IActionResult> Revert()
        {
            try
            {
                _scheduledJobExecutor.Cancel(Guid.Parse(SmoothDeployJobId));

                var languages = _languagesResolver.GetUsedLanguages()?.ToList() ?? new List<string>();
                var languageParams = string.Join(",", languages.Select(lang => $"\"{lang}\""));
                var query = $@"mutation {{ useOldAccountVersion(languages: [{languageParams}]) }}";
                var customHeaders = new Dictionary<string, string> { { "cg-use-new", "false" } };

                var response = await _client.QueryAsync(query, new { }, customHeaders: customHeaders);

                var state = _store.LoadJobState();
                state.RevertedBy = "util-api-debug";
                state.RevertedAt = DateTime.UtcNow;
                state.JobPhase = SmoothDeployJobPhases.IDLE;
                _store.SaveJobState(state);

                return Ok(new
                {
                    Step = "6 — Revert Deployment (useOldAccountVersion mutation)",
                    GraphQLMutation = query,
                    CustomHeaders = customHeaders,
                    RawResponse = TryParseJson(response),
                    NewPhase = state.JobPhaseReadable,
                    Explanation = "Reverted to the old slot. Phase reset to IDLE.",
                    CodeRef = "SmoothRebuildController.cs line 173-185, GraphQLService.cs line 58-70"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Abandon the new slot — calls abandonSlot mutation.
        /// This discards the Green slot and keeps the old Blue slot live.
        /// Sample usage: https://localhost:5009/util-api/custom-smooth-rebuild/abandon
        /// </summary>
        [HttpGet("abandon")]
        public async Task<IActionResult> Abandon()
        {
            try
            {
                _scheduledJobExecutor.Cancel(Guid.Parse(SmoothDeployJobId));

                var query = @"mutation { abandonSlot }";
                var response = await _client.QueryAsync(query, new { });

                var state = _store.LoadJobState();
                state.AbandonedBy = "util-api-debug";
                state.AbandonedAt = DateTime.UtcNow;
                state.JobPhase = SmoothDeployJobPhases.IDLE;
                _store.SaveJobState(state);

                return Ok(new
                {
                    Step = "7 — Abandon New Slot (abandonSlot mutation)",
                    GraphQLMutation = query,
                    RawResponse = TryParseJson(response),
                    NewPhase = state.JobPhaseReadable,
                    Explanation = "The Green slot has been abandoned. The old Blue slot remains live. Phase reset to IDLE.",
                    CodeRef = "GraphQLService.cs line 72-83, SmoothRebuildController.cs line 142-171"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 8: Force-reset the job state to IDLE. Useful for cleaning up stuck states.
        /// Sample usage: https://localhost:5009/util-api/custom-smooth-rebuild/reset-to-idle
        /// </summary>
        [HttpGet("reset-to-idle")]
        public IActionResult ResetToIdle()
        {
            try
            {
                var previousState = _store.LoadJobState();
                var previousPhase = previousState.JobPhaseReadable;

                _scheduledJobExecutor.Cancel(Guid.Parse(SmoothDeployJobId));

                var state = _store.LoadJobState();
                state.StoppedBy = "util-api-debug";
                state.StoppedAt = DateTime.UtcNow;
                state.StartedDeltaSyncAt = null;
                state.CommittedAt = null;
                state.RevertedAt = null;
                state.AbandonedAt = null;
                state.ItemsToSync = 0;
                state.LastProcessedActivityId = 0;
                state.JobPhase = SmoothDeployJobPhases.IDLE;
                state.LastError = null;
                _store.SaveJobState(state);

                return Ok(new
                {
                    Step = "8 — Reset to IDLE",
                    PreviousPhase = previousPhase,
                    NewPhase = "IDLE",
                    Explanation = "State has been force-reset to IDLE. All timestamps cleared. The scheduled job will now do nothing when executed.",
                    CodeRef = "SmoothRebuildController.cs line 102-122: Stop() method"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        #region Helpers

        private static object TryParseJson(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse))
                return new { IsEmpty = true, Raw = rawResponse };
            try
            {
                using var document = JsonDocument.Parse(rawResponse);
                return document.RootElement.Clone();
            }
            catch
            {
                return new { IsJson = false, Raw = rawResponse };
            }
        }

        #endregion
    }
}

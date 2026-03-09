# Continue

## Current State

- `TaskEvent` phase is in place locally:
  - models: `TaskEvent`, `TaskEventType`
  - service: `Services/TaskEventService.cs`
  - hooks already exist in `InteractionModule.cs` and `AutomationService.cs`
- Knowledge-phase models were added:
  - `MemberProfile`
  - `MemberDailySignal`
  - `TopicMention`
  - `DecisionLog`
  - `RiskLog`
  - `SprintDailySnapshot`
  - `ProjectRiskSnapshot`
- `BotDbContext.cs` already includes the new DbSets and entity config.
- `ProjectKnowledgeService.cs` was added and partially integrated.
- `ProjectWeeklyReviewService.cs` was added.
- `Program.cs` now registers:
  - `ProjectKnowledgeService`
  - `ProjectWeeklyReviewService`
  - and includes schema bootstrap for the new tables/column.
- `ProjectInsightService.cs` was updated to include `Knowledge` in `ProjectAssistantContext`.
- `NotificationService.cs` now has `SendProjectFeedAsync(...)`.
- `AutomationService.cs` now calls `TryRunWeeklyReviewAsync(...)`.
- `InteractionModule.cs` now has `/test weekly-review`.

## Important Note

- After the latest edits, I did **not** run a final `dotnet build`.
- Resume by building first and fixing compile errors before adding more features.

## Likely Next Steps

1. Run `dotnet build`.
2. Fix any breakages from:
   - new `ProjectAssistantContext` constructor shape
   - `ProjectKnowledgeService` helper logic
   - `ProjectWeeklyReviewService` wiring
3. Review `ProjectKnowledgeService.cs` heuristics:
   - standup lateness is still approximate
   - topic tagging is rule-based only
   - decision/risk extraction is still lightweight
4. Update `BotAssistantService.cs` to use:
   - `context.Knowledge.Members`
   - `context.Knowledge.Topics`
   - `context.Knowledge.Decisions`
   - `context.Knowledge.Risks`
   - `context.Knowledge.SprintTrend`
   - `context.Knowledge.RiskTrend`
5. Improve fallback answers with:
   - evidence
   - time range
   - confidence
6. Optionally enrich `ProjectDailyLeadReportService.cs` with:
   - topic summary
   - decision highlights
   - risk trend
7. Test on Discord:
   - `@bot ai dang qua tai`
   - `@bot tuan qua team ban gi`
   - `@bot quyet dinh moi nhat la gi`
   - `/test weekly-review`

## Files To Check First

- `project-manager-bot/Services/ProjectKnowledgeService.cs`
- `project-manager-bot/Services/ProjectInsightService.cs`
- `project-manager-bot/Services/ProjectWeeklyReviewService.cs`
- `project-manager-bot/Program.cs`
- `project-manager-bot/Modules/InteractionModule.cs`
- `project-manager-bot/Services/BotAssistantService.cs`


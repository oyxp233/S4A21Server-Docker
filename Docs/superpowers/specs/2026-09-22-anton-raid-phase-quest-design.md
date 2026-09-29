# Anton raid phase quest progress

## Evidence and goal

The current Script.pvf defines quest 12830 (`anton_epic_acce_01.qst`) as
`[raid phase clear]`, with `[int data] 0 5 -1 1 5 -1`. Its two displayed
objectives are five successful Anton raid phase-one and phase-two clears.
`QuestData.GetInitTrigger` currently falls back to 1 for this type, and the
raid phase completion path does not submit quest progress.

## Behavior

- Parse only valid three-integer phase targets for this quest type. Pack their
  required remaining counts into the existing nine-bit quest trigger channels.
- On a successful phase completion, apply one decrement to the matching
  channel for each frozen eligible raid participant with an active quest.
- Use a deterministic event ID from raid instance and phase, with the
  existing quest progress inbox and activation ID to reject duplicate delivery.
- Repair legacy active trigger `1` for this quest type when the first valid
  phase completion is applied, before decrementing. Preserve all other
  trigger values.
- Ignore failed phases, unrelated quests, nonparticipants and replayed phase
  completions. Deliver refreshed quest progress to current sessions. Persist
  eligible offline participants' progress so reconnect shows it.
- Do not modify PVF, schema, raid reward eligibility, or quest rewards.

## Verification

Use a temporary SQLite test database and the supplied PVF. Verify acceptance
starts at five/five, each successful phase decrements only its channel,
duplicate events have no effect, a legacy trigger is repaired, and invalid
phase data or failed/noneligible transitions cannot grant progress. Run the
focused quest/raid self-tests, then the full self-test suite and rebuild.

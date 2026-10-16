# Spec 001: Waitlist and automatic promotion

**Status:** shipped in 1.0.0 · **Owner:** Shovan Dutta

## Problem

Popular events fill up within hours, and instructors keep a spreadsheet of people who asked for a seat. When someone drops out the seat stays empty, because nobody checks the spreadsheet in time.

## Change

When an open event is full, a new enrollment joins a waitlist instead of failing. Whenever a seat frees up, the oldest waitlisted learner takes it automatically, provided the organization has the credits for it.

## Acceptance criteria

| # | Criterion | Test |
|---|---|---|
| AC1 | Enrolling into a full open event returns `201` with `status: waitlisted` and the learner's `waitlistPosition`. No credits are charged. | `Parallel_enrollments_never_oversell_seats_or_credits` |
| AC2 | Waitlist positions are 1, 2, 3… in the order learners joined, with no gaps. | `Parallel_enrollments_never_oversell_seats_or_credits` |
| AC3 | Cancelling an enrolled learner refunds their credits and promotes the first waitlisted learner in the same transaction. Everyone behind them moves up one. | `Cancelling_an_enrollment_refunds_it_and_promotes_the_first_waitlisted_learner` |
| AC4 | Raising an open event's capacity promotes as many waitlisted learners as now fit. | `Adding_seats_fills_them_from_the_waitlist` |
| AC5 | Promotion stops when the organization runs out of credits. The learner stays waitlisted rather than being seated unpaid. | `dbo.usp_PromoteWaitlist` caps promotions at `Balance / CreditsPerSeat` |
| AC6 | Concurrent enrollments never seat more learners than the capacity. | `Parallel_enrollments_never_oversell_seats_or_credits` (25 requests, 5 seats) |
| AC7 | The roster lists enrolled learners first, then the waitlist in position order. Searching the roster does not renumber positions. | `Cancelling_an_enrollment_…`, `RosterComponent` view |
| AC8 | The UI says "on the waitlist at position N" after enrolling, and the roster updates without a reload. | `a full event waitlists new learners and fills a freed seat from the waitlist` (Playwright) |

## Design

- `dbo.usp_PromoteWaitlist` is internal (dbo schema, not granted to the app). It is called only from procedures that already hold the event row lock: cancelling an enrollment, raising capacity, and reopening an event.
- Positions are computed by `dbo.EnrollmentView` with `ROW_NUMBER() OVER (PARTITION BY EventId, Status ORDER BY CreatedAt, EnrollmentId)`, so they cannot drift out of step with stored data.
- Lock order is unchanged: event row, then enrollments, then `TenantCredits`.

## Out of scope

- Notifying promoted learners by email.
- Letting a learner leave the waitlist themselves.

## Rollout

No new tables; `R__` procedure changes only. Rolling back the release restores the previous procedures, and waitlisted rows stay valid.

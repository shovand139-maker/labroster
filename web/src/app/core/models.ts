// Shapes returned by the LabRoster API (src/LabRoster.Api/Contracts/Models.cs).

export type EventStatus = 'draft' | 'open' | 'closed' | 'cancelled';
export type EnrollmentStatus = 'enrolled' | 'waitlisted' | 'cancelled';
export type Role = 'Admin' | 'Instructor' | 'Viewer';

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface LabEvent {
  id: string;
  title: string;
  templateId: string;
  templateTitle: string;
  cloud: 'azure' | 'aws' | 'gcp';
  creditsPerSeat: number;
  startsAt: string;
  endsAt: string;
  capacity: number;
  enrolled: number;
  waitlisted: number;
  seatsLeft: number;
  status: EventStatus;
  createdBy: string;
  createdAt: string;
  updatedAt: string;
  eTag: string;
}

export interface EventInput {
  title: string;
  templateId?: string | null;
  startsAt: string;
  endsAt: string;
  capacity: number;
}

export interface Enrollment {
  id: string;
  eventId: string;
  email: string;
  fullName: string;
  status: EnrollmentStatus;
  creditsCharged: number;
  waitlistPosition: number | null;
  createdAt: string;
  statusChangedAt: string;
}

export interface BulkRow {
  row: number;
  email: string;
  outcome: string;
  enrollmentId: string | null;
}

export interface BulkResult {
  enrolled: number;
  waitlisted: number;
  skipped: number;
  rows: BulkRow[];
}

export interface CancelResult {
  enrollmentId: string;
  eventId: string;
  previousStatus: EnrollmentStatus;
  creditsRefunded: number;
  promotedFromWaitlist: number;
}

export interface LabTemplate {
  id: string;
  title: string;
  cloud: 'azure' | 'aws' | 'gcp';
  durationMinutes: number;
  creditsPerSeat: number;
}

export interface LedgerEntry {
  id: number;
  delta: number;
  reason: 'allocation' | 'enrollment' | 'refund';
  eventId: string | null;
  eventTitle: string | null;
  createdAt: string;
}

export interface Credits {
  balance: number;
  allocated: number;
  spent30Days: number;
  refunded30Days: number;
  recent: LedgerEntry[];
}

export interface AuditEntry {
  id: number;
  actor: string;
  action: string;
  entityId: string | null;
  details: Record<string, unknown> | null;
  createdAt: string;
}

export interface Me {
  userId: string;
  name: string;
  role: Role;
  tenantId: string;
  tenantName: string;
  tenantSlug: string;
  creditBalance: number;
  permissions: { manageEvents: boolean; readAudit: boolean };
}

export interface DevUser {
  id: string;
  name: string;
  email: string;
  role: Role;
  tenantName: string;
}

export interface DevToken {
  accessToken: string;
  expiresAt: string;
  user: DevUser;
}

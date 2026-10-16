import { Routes } from '@angular/router';
import { auditGuard, signedInGuard } from './core/auth';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'events' },
  {
    path: 'sign-in',
    title: 'Sign in · LabRoster',
    loadComponent: () => import('./features/sign-in/sign-in.page').then((m) => m.SignInPage),
  },
  {
    path: '',
    canActivate: [signedInGuard],
    children: [
      {
        path: 'events',
        title: 'Events · LabRoster',
        loadComponent: () => import('./features/events/event-list.page').then((m) => m.EventListPage),
      },
      {
        path: 'events/new',
        title: 'New event · LabRoster',
        loadComponent: () => import('./features/events/event-form.page').then((m) => m.EventFormPage),
      },
      {
        path: 'events/:id',
        title: 'Event · LabRoster',
        loadComponent: () => import('./features/events/event-detail.page').then((m) => m.EventDetailPage),
      },
      {
        path: 'credits',
        title: 'Credits · LabRoster',
        loadComponent: () => import('./features/credits/credits.page').then((m) => m.CreditsPage),
      },
      {
        path: 'audit',
        title: 'Audit log · LabRoster',
        canActivate: [auditGuard],
        loadComponent: () => import('./features/audit/audit.page').then((m) => m.AuditPage),
      },
    ],
  },
  { path: '**', redirectTo: 'events' },
];

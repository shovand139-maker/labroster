import { expect, test, type Page } from '@playwright/test';

async function signInAs(page: Page, name: string): Promise<void> {
  await page.goto('/sign-in');
  await page.getByRole('button', { name: new RegExp(name) }).click();
  await expect(page.getByRole('heading', { name: 'Events', level: 1 })).toBeVisible();
}

function rosterRow(page: Page, email: string) {
  return page.locator(`tr[data-email="${email}"]`);
}

async function creditBalance(page: Page): Promise<number> {
  const text = await page.getByTestId('credit-balance').innerText();
  return Number(/\d+/.exec(text)?.[0]);
}

async function enroll(page: Page, fullName: string, email: string): Promise<void> {
  await page.getByLabel('Full name').fill(fullName);
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.getByRole('button', { name: 'Enroll', exact: true }).click();
}

test('an instructor only sees events from their own organization', async ({ page }) => {
  await signInAs(page, 'Arjun Bhatt');

  const list = page.getByTestId('event-list');
  await expect(page.getByTestId('tenant-name')).toHaveText('Contoso Learning');
  await expect(list).toContainText('AZ-104 Bootcamp, Cohort 12');
  await expect(list).toContainText('Kubernetes on AKS Workshop');
  await expect(list).not.toContainText('GKE Fundamentals');
});

test('a full event waitlists new learners and fills a freed seat from the waitlist', async ({ page }) => {
  await signInAs(page, 'Arjun Bhatt');
  await page.getByRole('link', { name: /Kubernetes on AKS Workshop/ }).click();
  await expect(page.getByTestId('seats-taken')).toHaveText('8/8');

  await enroll(page, 'Riya Kapoor', 'riya.kapoor@contoso.example');

  await expect(page.getByTestId('enroll-notice')).toContainText('waitlist at position 4');
  await expect(rosterRow(page, 'riya.kapoor@contoso.example').getByTestId('enrollment-status')).toContainText('Waitlisted #4');

  const leaving = rosterRow(page, 'ananya.ghosh@contoso.example');
  await leaving.getByRole('button', { name: 'Cancel seat' }).click();
  await leaving.getByRole('button', { name: 'Confirm cancel' }).click();

  await expect(leaving.getByTestId('enrollment-status')).toContainText('Cancelled');
  await expect(rosterRow(page, 'nikhil.joshi@contoso.example').getByTestId('enrollment-status')).toContainText('Enrolled');
  await expect(rosterRow(page, 'riya.kapoor@contoso.example').getByTestId('enrollment-status')).toContainText('Waitlisted #3');
  await expect(page.getByTestId('seats-taken')).toHaveText('8/8');
});

test('an instructor creates an event, opens it and pays for a seat from the credit balance', async ({ page }) => {
  await signInAs(page, 'Arjun Bhatt');
  const before = await creditBalance(page);

  await page.getByRole('link', { name: 'New event' }).click();
  await page.getByLabel('Title').fill('Terraform on Azure, Pilot');
  await page.getByLabel('Lab template').selectOption({ index: 1 });
  await page.getByLabel('Seats').fill('2');
  await page.getByRole('button', { name: 'Create draft event' }).click();

  await expect(page.getByTestId('event-title')).toHaveText('Terraform on Azure, Pilot');
  await expect(page.getByTestId('event-status')).toHaveText('Draft');
  await page.getByRole('button', { name: 'Open for enrollment' }).click();
  await expect(page.getByTestId('event-status')).toHaveText('Open');

  await enroll(page, 'Asha Rao', 'asha.rao@contoso.example');

  await expect(page.getByTestId('enroll-notice')).toHaveText('Asha Rao is enrolled.');
  await expect(page.getByTestId('seats-taken')).toHaveText('1/2');
  await expect(page.getByTestId('credit-balance')).toHaveText(`${before - 20} credits`);
});

test('the form shows the API’s validation errors next to the fields', async ({ page }) => {
  await signInAs(page, 'Arjun Bhatt');
  await page.getByRole('link', { name: 'New event' }).click();

  await page.getByLabel('Title').fill('Too long');
  await page.getByLabel('Lab template').selectOption({ index: 1 });
  const start = await page.getByLabel('Starts').inputValue();
  const end = new Date(new Date(start).getTime() + 20 * 24 * 3_600_000);
  const pad = (n: number) => String(n).padStart(2, '0');
  await page
    .getByLabel('Ends')
    .fill(`${end.getFullYear()}-${pad(end.getMonth() + 1)}-${pad(end.getDate())}T${pad(end.getHours())}:${pad(end.getMinutes())}`);
  await page.getByRole('button', { name: 'Create draft event' }).click();

  await expect(page.getByText('An event can run for at most 14 days.')).toBeVisible();
});

test('a viewer can read the roster but cannot change it', async ({ page }) => {
  await signInAs(page, 'Meera Nair');

  await expect(page.getByRole('link', { name: 'New event' })).toHaveCount(0);
  await page.getByRole('link', { name: /AZ-104 Bootcamp, Cohort 12/ }).click();

  await expect(page.getByTestId('roster-table')).toContainText('Aarav Mehta');
  await expect(page.getByRole('button', { name: 'Cancel seat' })).toHaveCount(0);
  await expect(page.getByLabel('Full name')).toHaveCount(0);
});

test('a stale edit is refused and the newer version is shown instead', async ({ browser }) => {
  const mine = await (await browser.newContext()).newPage();
  const theirs = await (await browser.newContext()).newPage();
  await signInAs(mine, 'Arjun Bhatt');
  await signInAs(theirs, 'Priya Raman');

  for (const page of [mine, theirs]) {
    await page.getByRole('link', { name: /Serverless on AWS Lambda/ }).click();
    await page.getByRole('button', { name: 'Edit details' }).click();
  }

  await theirs.getByLabel('Seats').fill('30');
  await theirs.getByRole('button', { name: 'Save changes' }).click();
  await expect(theirs.getByTestId('seats-taken')).toHaveText('0/30');

  await mine.getByLabel('Seats').fill('25');
  await mine.getByRole('button', { name: 'Save changes' }).click();

  await expect(mine.getByText('Someone else changed this event')).toBeVisible();
  await expect(mine.getByTestId('seats-taken')).toHaveText('0/30');
});

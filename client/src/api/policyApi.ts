const API_BASE = 'http://localhost:5035/api';

export const ENVIRONMENTS = ['dev', 'dev3', 'tst', 'tst2', 'acc'] as const;
export type Environment = (typeof ENVIRONMENTS)[number];

export async function fetchFieldValues(
  formNumber: string,
  editionDate: string,
  policyNumber: string,
  env: Environment = 'tst',
): Promise<Record<string, string>> {
  const res = await fetch(`${API_BASE}/policy/field-values`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ formNumber, editionDate, policyNumber, env }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? `Policy field-values failed: ${res.statusText}`);
  }
  return res.json();
}

export async function populateForm(
  formNumber: string,
  editionDate: string,
  policyNumber: string,
  env: Environment = 'tst',
  flatten = true,
): Promise<Blob> {
  const res = await fetch(`${API_BASE}/policy/populate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ formNumber, editionDate, policyNumber, env, flatten }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? `Populate failed: ${res.statusText}`);
  }
  return res.blob();
}

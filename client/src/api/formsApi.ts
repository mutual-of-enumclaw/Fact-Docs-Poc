const API_BASE = 'http://localhost:5035/api';

export interface FormCatalogEntry {
  formKey: string;
  fileName: string;
  sectionType: string;
  sectionCount: number;
  displayName?: string;
  sourceFormNumber?: string;
  sourceEditionDate?: string;
  description?: string;
}

export interface FormSectionInfo {
  fileName: string;
  sectionType: string;
  metadata: string;
}

export interface FormInfoResponse {
  formKey: string;
  fileName: string;
  fieldCount: number;
  staticTextCount: number;
  lineCount: number;
  textAreaCount: number;
  pageCount: number;
  fieldNames: string[];
  sections: FormSectionInfo[];
}

export async function fetchForms(search?: string): Promise<FormCatalogEntry[]> {
  const params = search ? `?search=${encodeURIComponent(search)}` : '';
  const res = await fetch(`${API_BASE}/forms${params}`);
  if (!res.ok) throw new Error(`Failed to fetch forms: ${res.statusText}`);
  return res.json();
}

export async function fetchFormInfo(formNumber: string, editionDate: string): Promise<FormInfoResponse> {
  const res = await fetch(`${API_BASE}/forms/${encodeURIComponent(formNumber)}/${encodeURIComponent(editionDate)}/info`);
  if (!res.ok) throw new Error(`Failed to fetch form info: ${res.statusText}`);
  return res.json();
}

export interface FieldDescriptor {
  name: string;
  page: number;
  x: number;
  y: number;
  width: number;
  height: number;
  fontId: number;
  pointSize: number;
  bold: boolean;
  maxLength: number;
  ddtMethod: string;
  ddtSource: string;
}

export interface FormFieldsResponse {
  formKey: string;
  fileName: string;
  pageCount: number;
  fields: FieldDescriptor[];
}

export async function fetchFormFields(formNumber: string, editionDate: string): Promise<FormFieldsResponse> {
  const res = await fetch(`${API_BASE}/forms/${encodeURIComponent(formNumber)}/${encodeURIComponent(editionDate)}/fields`);
  if (!res.ok) throw new Error(`Failed to fetch form fields: ${res.statusText}`);
  return res.json();
}

export async function convertForm(formNumber: string, editionDate: string): Promise<Blob> {
  const res = await fetch(`${API_BASE}/convert`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ formNumber, editionDate }),
  });
  if (!res.ok) throw new Error(`Conversion failed: ${res.statusText}`);
  return new Blob([await res.arrayBuffer()], { type: 'application/pdf' });
}

export async function convertAndFill(
  formNumber: string,
  editionDate: string,
  fieldValues: Record<string, string>,
  flatten: boolean = false,
): Promise<Blob> {
  const res = await fetch(`${API_BASE}/convert/fill`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ formNumber, editionDate, fieldValues, flatten }),
  });
  if (!res.ok) throw new Error(`Fill failed: ${res.statusText}`);
  return new Blob([await res.arrayBuffer()], { type: 'application/pdf' });
}

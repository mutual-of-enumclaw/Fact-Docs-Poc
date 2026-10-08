import { useState, useEffect, useCallback } from 'react';
import { Card, Form, Input, Button, Space, Typography, Descriptions, Spin, message, List, Checkbox, Tooltip, Empty, Select, Modal } from 'antd';
import { DownloadOutlined, EyeOutlined, EditOutlined, UserOutlined, SaveOutlined, FormOutlined } from '@ant-design/icons';
import { useSearchParams, useNavigate } from 'react-router-dom';
import {
  convertForm,
  convertAndFill,
  fetchFormInfo,
  fetchFormFields,
  exportGhostDraft,
  type FormInfoResponse,
  type FieldDescriptor,
} from '../api/formsApi';
import { fetchFieldValues, ENVIRONMENTS, type Environment } from '../api/policyApi';
import { createScenario } from '../api/scenariosApi';
import { importLegacy } from '../api/definitionsApi';

const { Title } = Typography;

export default function ConvertPage() {
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const [formNumber, setFormNumber] = useState(searchParams.get('form') ?? '');
  const [editionDate, setEditionDate] = useState(searchParams.get('edition') ?? '');
  const [formInfo, setFormInfo] = useState<FormInfoResponse | null>(null);
  const [pdfUrl, setPdfUrl] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [infoLoading, setInfoLoading] = useState(false);
  const [fields, setFields] = useState<FieldDescriptor[]>([]);
  const [fieldValues, setFieldValues] = useState<Record<string, string>>({});
  const [flatten, setFlatten] = useState(true);
  const [filling, setFilling] = useState(false);
  const [isFilled, setIsFilled] = useState(false);

  // Policy loading
  const [policyNumber, setPolicyNumber] = useState('');
  const [policyEnv, setPolicyEnv] = useState<Environment>('tst');
  const [policyLoading, setPolicyLoading] = useState(false);

  // Open in designer
  const [openingDesigner, setOpeningDesigner] = useState(false);

  // Export to GhostDraft
  const [exportingGd, setExportingGd] = useState(false);

  // Save as scenario
  const [scenarioModalOpen, setScenarioModalOpen] = useState(false);
  const [scenarioName, setScenarioName] = useState('');
  const [savingScenario, setSavingScenario] = useState(false);

  // Cleanup blob URL on unmount
  useEffect(() => {
    return () => {
      if (pdfUrl) URL.revokeObjectURL(pdfUrl);
    };
  }, [pdfUrl]);

  // Auto-fetch info and convert if params provided (e.g. navigated from the catalog)
  useEffect(() => {
    const form = searchParams.get('form');
    const edition = searchParams.get('edition');
    if (form && edition) {
      setFormNumber(form);
      setEditionDate(edition);
      runConvert(form, edition);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const loadInfo = useCallback(async (form: string, edition: string) => {
    if (!form || !edition) return;
    setInfoLoading(true);
    try {
      const info = await fetchFormInfo(form, edition);
      setFormInfo(info);
      if (info.fieldCount > 0) {
        try {
          const f = await fetchFormFields(form, edition);
          setFields(f.fields);
          setFieldValues({});
        } catch {
          setFields([]);
        }
      } else {
        setFields([]);
        setFieldValues({});
      }
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Unknown error';
      message.error(msg);
    } finally {
      setInfoLoading(false);
    }
  }, []);

  const handleFill = async () => {
    if (!formNumber) {
      message.warning('Enter a form number');
      return;
    }
    setFilling(true);
    try {
      if (pdfUrl) URL.revokeObjectURL(pdfUrl);
      const blob = await convertAndFill(formNumber, editionDate, fieldValues, flatten);
      const url = URL.createObjectURL(blob);
      setPdfUrl(url);
      setIsFilled(true);
      message.success(flatten ? 'Filled & flattened PDF generated' : 'Filled (interactive) PDF generated');
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Fill failed';
      message.error(msg);
    } finally {
      setFilling(false);
    }
  };

  const runConvert = useCallback(async (form: string, edition: string) => {
    if (!form) {
      message.warning('Enter a form number or FAP file name');
      return;
    }
    setLoading(true);
    try {
      const blob = await convertForm(form, edition);
      const url = URL.createObjectURL(blob);
      setPdfUrl((prev) => {
        if (prev) URL.revokeObjectURL(prev);
        return url;
      });
      setIsFilled(false);
      await loadInfo(form, edition);
      message.success('PDF generated successfully');
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Conversion failed';
      message.error(msg);
    } finally {
      setLoading(false);
    }
  }, [loadInfo]);

  const handleConvert = () => runConvert(formNumber, editionDate);

  const handleLoadFromPolicy = async () => {
    if (!formNumber || !editionDate) { message.warning('Convert a form first'); return; }
    if (!policyNumber) { message.warning('Enter a policy number'); return; }
    setPolicyLoading(true);
    try {
      const values = await fetchFieldValues(formNumber, editionDate, policyNumber, policyEnv);
      setFieldValues(values);
      message.success(`Loaded ${Object.keys(values).length} field values from policy ${policyNumber}`);
    } catch (e: unknown) {
      const err = e instanceof Error ? e.message : 'Policy load failed';
      if (err.includes('No field map')) {
        message.warning(`No field map registered for ${formNumber} ${editionDate} — fill manually.`);
      } else {
        message.error(err);
      }
    } finally {
      setPolicyLoading(false);
    }
  };

  const handleSaveScenario = async () => {
    if (!scenarioName) { message.warning('Enter a name for the scenario'); return; }
    if (!formNumber || !editionDate) { message.warning('No form loaded'); return; }
    setSavingScenario(true);
    try {
      await createScenario({ name: scenarioName, formNumber, editionDate, fieldValues });
      setScenarioModalOpen(false);
      setScenarioName('');
      message.success('Scenario saved');
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Save failed');
    } finally {
      setSavingScenario(false);
    }
  };

  const handleOpenInDesigner = async () => {
    if (!formNumber) { message.warning('No form loaded'); return; }
    setOpeningDesigner(true);
    try {
      const def = await importLegacy(formNumber, editionDate ?? '');
      navigate(`/design?id=${def.id}`);
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Failed to open in designer');
    } finally {
      setOpeningDesigner(false);
    }
  };

  const handleExportGhostDraft = async () => {
    if (!formNumber) { message.warning('No form loaded'); return; }
    setExportingGd(true);
    try {
      const blob = await exportGhostDraft(formNumber, editionDate ?? '');
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      const base = formInfo?.fileName ?? formNumber;
      a.download = `${base}.gd`;
      a.click();
      URL.revokeObjectURL(url);
      message.success('GhostDraft .gd file downloaded');
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Export failed');
    } finally {
      setExportingGd(false);
    }
  };

  const handleDownload = () => {
    if (!pdfUrl) return;
    const a = document.createElement('a');
    a.href = pdfUrl;
    const base = formInfo?.fileName ?? formNumber;
    const suffix = isFilled ? (flatten ? '_filled_flat' : '_filled') : '';
    a.download = `${base}${suffix}.pdf`;
    a.click();
  };

  return (
    <div style={{ display: 'flex', gap: 24, flexWrap: 'wrap' }}>
      {/* Left panel: controls */}
      <div style={{ flex: '0 0 400px', maxWidth: 400 }}>
        <Title level={3}>Convert FAP → PDF</Title>
        <Card>
          <Form layout="vertical">
            <Form.Item label="Form Number" required>
              <Input
                placeholder="e.g. CA0001, EA4605"
                value={formNumber}
                onChange={(e) => setFormNumber(e.target.value.toUpperCase())}
              />
            </Form.Item>
            <Form.Item label="Edition Date" required>
              <Input
                placeholder="e.g. 0306, 1013"
                value={editionDate}
                onChange={(e) => setEditionDate(e.target.value)}
              />
            </Form.Item>
            <Space>
              <Button
                type="primary"
                icon={<EyeOutlined />}
                loading={loading}
                onClick={handleConvert}
              >
                Convert & Preview
              </Button>
              <Button
                icon={<DownloadOutlined />}
                disabled={!pdfUrl}
                onClick={handleDownload}
              >
                Download
              </Button>
              <Tooltip title="Import this form into the Designer as individually editable elements">
                <Button
                  icon={<FormOutlined />}
                  disabled={!formNumber}
                  loading={openingDesigner}
                  onClick={handleOpenInDesigner}
                >
                  Open in Designer
                </Button>
              </Tooltip>
              <Tooltip title="Export as a GhostDraft .gd file (import into GhostDraft Designer)">
                <Button
                  icon={<DownloadOutlined />}
                  disabled={!formNumber}
                  loading={exportingGd}
                  onClick={handleExportGhostDraft}
                >
                  Export .gd
                </Button>
              </Tooltip>
            </Space>
          </Form>
        </Card>

        {infoLoading && <Spin style={{ marginTop: 16 }} />}
        {formInfo && (
          <Card title="Form Details" style={{ marginTop: 16 }}>
            <Descriptions column={1} size="small">
              <Descriptions.Item label="Form Key">{formInfo.formKey}</Descriptions.Item>
              <Descriptions.Item label="File Name">{formInfo.fileName}</Descriptions.Item>
              <Descriptions.Item label="Pages">{formInfo.pageCount}</Descriptions.Item>
              <Descriptions.Item label="Fields">{formInfo.fieldCount}</Descriptions.Item>
              <Descriptions.Item label="Static Texts">{formInfo.staticTextCount}</Descriptions.Item>
              <Descriptions.Item label="Lines/Boxes">{formInfo.lineCount}</Descriptions.Item>
              <Descriptions.Item label="Text Areas">{formInfo.textAreaCount}</Descriptions.Item>
            </Descriptions>
            {formInfo.fieldNames.length > 0 && (
              <div style={{ marginTop: 12 }}>
                <strong>Fields ({formInfo.fieldNames.length}):</strong>
                <List
                  size="small"
                  style={{ maxHeight: 300, overflow: 'auto', marginTop: 8 }}
                  dataSource={formInfo.fieldNames}
                  renderItem={(name) => <List.Item style={{ padding: '4px 0' }}>{name}</List.Item>}
                />
              </div>
            )}
          </Card>
        )}

        {formInfo && formInfo.fieldCount > 0 && (
          <Card
            title={`Fill Fields (${fields.length})`}
            style={{ marginTop: 16 }}
            extra={
              <Button size="small" onClick={() => setFieldValues({})}>
                Clear
              </Button>
            }
          >
            {fields.length === 0 ? (
              <Empty description="Loading field metadata…" image={Empty.PRESENTED_IMAGE_SIMPLE} />
            ) : (
              <>
                <div style={{ maxHeight: 360, overflow: 'auto', paddingRight: 4 }}>
                  {fields.map((f) => (
                    <Form.Item
                      key={`${f.page}-${f.name}`}
                      label={
                        <Tooltip
                          title={`Page ${f.page} · font ${f.fontId} ${f.pointSize}pt${f.bold ? ' bold' : ''}${
                            f.ddtMethod ? ` · DDT ${f.ddtMethod}` : ''
                          }`}
                        >
                          <span>
                            {f.name}
                            {f.maxLength > 0 && (
                              <Typography.Text type="secondary"> ({f.maxLength})</Typography.Text>
                            )}
                          </span>
                        </Tooltip>
                      }
                      style={{ marginBottom: 8 }}
                    >
                      <Input
                        size="small"
                        maxLength={f.maxLength > 0 ? f.maxLength : undefined}
                        value={fieldValues[f.name] ?? ''}
                        onChange={(e) =>
                          setFieldValues((prev) => ({ ...prev, [f.name]: e.target.value }))
                        }
                      />
                    </Form.Item>
                  ))}
                </div>
                <Checkbox
                  checked={flatten}
                  onChange={(e) => setFlatten(e.target.checked)}
                  style={{ marginBottom: 12 }}
                >
                  Flatten (burn values in — non-editable, matches print)
                </Checkbox>
                <Space wrap>
                  <Button
                    type="primary"
                    icon={<EditOutlined />}
                    loading={filling}
                    onClick={handleFill}
                  >
                    Fill &amp; Preview
                  </Button>
                  <Button
                    icon={<SaveOutlined />}
                    onClick={() => setScenarioModalOpen(true)}
                  >
                    Save as Scenario
                  </Button>
                </Space>
              </>
            )}
          </Card>
        )}

        {/* Load from policy card */}
        {formInfo && (
          <Card title={<><UserOutlined /> Load from Policy</>} style={{ marginTop: 16 }}>
            <Form layout="vertical" size="small">
              <Form.Item label="Environment">
                <Select value={policyEnv} onChange={setPolicyEnv} style={{ width: '100%' }}>
                  {ENVIRONMENTS.map((e) => (
                    <Select.Option key={e} value={e}>{e}</Select.Option>
                  ))}
                </Select>
              </Form.Item>
              <Form.Item label="Policy Number">
                <Input
                  placeholder="e.g. BOP00123456"
                  value={policyNumber}
                  onChange={(e) => setPolicyNumber(e.target.value)}
                />
              </Form.Item>
              <Button
                type="primary"
                icon={<UserOutlined />}
                loading={policyLoading}
                onClick={handleLoadFromPolicy}
                block
              >
                Load Field Values
              </Button>
            </Form>
          </Card>
        )}
      </div>

      {/* Save as scenario modal */}
      <Modal
        title="Save as Scenario"
        open={scenarioModalOpen}
        onCancel={() => setScenarioModalOpen(false)}
        onOk={handleSaveScenario}
        confirmLoading={savingScenario}
        okText="Save"
      >
        <Form layout="vertical">
          <Form.Item label="Scenario Name" required>
            <Input
              placeholder="e.g. BOP standard renewal"
              value={scenarioName}
              onChange={(e) => setScenarioName(e.target.value)}
            />
          </Form.Item>
        </Form>
      </Modal>

      {/* Right panel: PDF preview */}
      <div style={{ flex: 1, minWidth: 500 }}>
        {pdfUrl ? (
          <div style={{ height: 'calc(100vh - 160px)', border: '1px solid #d9d9d9', borderRadius: 8, overflow: 'hidden' }}>
            <iframe
              src={pdfUrl}
              width="100%"
              height="100%"
              style={{ border: 'none' }}
              title="PDF Preview"
            />
          </div>
        ) : (
          <Card style={{ height: 400, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <Typography.Text type="secondary" style={{ fontSize: 16 }}>
              Convert a form to see the PDF preview here
            </Typography.Text>
          </Card>
        )}
      </div>
    </div>
  );
}

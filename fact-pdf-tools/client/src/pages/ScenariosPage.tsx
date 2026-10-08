import { useState, useCallback } from 'react';
import {
  Table, Button, Space, Typography, Tag, Modal, Form, Input, Spin, message,
  Drawer, Tooltip, Popconfirm, Empty,
} from 'antd';
import { PlusOutlined, PlayCircleOutlined, EditOutlined, DeleteOutlined } from '@ant-design/icons';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Worker, Viewer, SpecialZoomLevel } from '@react-pdf-viewer/core';
import { defaultLayoutPlugin } from '@react-pdf-viewer/default-layout';
import '@react-pdf-viewer/core/lib/styles/index.css';
import '@react-pdf-viewer/default-layout/lib/styles/index.css';
import {
  fetchScenarios, createScenario, updateScenario, deleteScenario, runScenario,
  type FormScenario,
} from '../api/scenariosApi';

const { Title, Text } = Typography;

function FieldValueEditor({
  values,
  onChange,
}: {
  values: Record<string, string>;
  onChange: (v: Record<string, string>) => void;
}) {
  const [newKey, setNewKey] = useState('');
  const [newVal, setNewVal] = useState('');

  const addEntry = () => {
    if (!newKey) return;
    onChange({ ...values, [newKey]: newVal });
    setNewKey('');
    setNewVal('');
  };

  return (
    <div>
      {Object.entries(values).map(([k, v]) => (
        <div key={k} style={{ display: 'flex', gap: 8, marginBottom: 6 }}>
          <Input size="small" value={k} style={{ width: 160, fontFamily: 'monospace', background: '#fafafa' }} readOnly />
          <Input
            size="small"
            value={v}
            style={{ flex: 1 }}
            onChange={(e) => onChange({ ...values, [k]: e.target.value })}
          />
          <Button size="small" danger onClick={() => {
            const next = { ...values };
            delete next[k];
            onChange(next);
          }}>×</Button>
        </div>
      ))}
      <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
        <Input size="small" placeholder="Field name" value={newKey} onChange={(e) => setNewKey(e.target.value)} style={{ width: 160, fontFamily: 'monospace' }} />
        <Input size="small" placeholder="Value" value={newVal} onChange={(e) => setNewVal(e.target.value)} style={{ flex: 1 }} />
        <Button size="small" onClick={addEntry} icon={<PlusOutlined />}>Add</Button>
      </div>
    </div>
  );
}

export default function ScenariosPage() {
  const queryClient = useQueryClient();
  const defaultLayoutPluginInstance = defaultLayoutPlugin();

  const [modalOpen, setModalOpen] = useState(false);
  const [editScenario, setEditScenario] = useState<FormScenario | null>(null);
  const [formState, setFormState] = useState({ name: '', formNumber: '', editionDate: '', fieldValues: {} as Record<string, string> });
  const [pdfUrl, setPdfUrl] = useState<string | null>(null);
  const [previewTitle, setPreviewTitle] = useState('');
  const [previewOpen, setPreviewOpen] = useState(false);
  const [runningId, setRunningId] = useState<string | null>(null);

  const { data: scenarios = [], isLoading } = useQuery({
    queryKey: ['scenarios'],
    queryFn: fetchScenarios,
    staleTime: 30_000,
  });

  const saveMutation = useMutation({
    mutationFn: (s: FormScenario | Omit<FormScenario, 'id' | 'createdAt' | 'updatedAt'>) =>
      'id' in s ? updateScenario(s as FormScenario) : createScenario(s),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['scenarios'] });
      setModalOpen(false);
      message.success('Scenario saved');
    },
    onError: (e: unknown) => message.error(e instanceof Error ? e.message : 'Save failed'),
  });

  const deleteMutation = useMutation({
    mutationFn: deleteScenario,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['scenarios'] });
      message.success('Deleted');
    },
    onError: (e: unknown) => message.error(e instanceof Error ? e.message : 'Delete failed'),
  });

  const openCreate = () => {
    setEditScenario(null);
    setFormState({ name: '', formNumber: '', editionDate: '', fieldValues: {} });
    setModalOpen(true);
  };

  const openEdit = (s: FormScenario) => {
    setEditScenario(s);
    setFormState({ name: s.name, formNumber: s.formNumber, editionDate: s.editionDate, fieldValues: { ...s.fieldValues } });
    setModalOpen(true);
  };

  const handleModalOk = () => {
    if (!formState.name || !formState.formNumber || !formState.editionDate) {
      message.warning('Name, form number, and edition date are required');
      return;
    }
    if (editScenario) {
      saveMutation.mutate({ ...editScenario, ...formState });
    } else {
      saveMutation.mutate(formState);
    }
  };

  const handleRun = useCallback(async (id: string, name: string) => {
    setRunningId(id);
    try {
      const blob = await runScenario(id);
      if (pdfUrl) URL.revokeObjectURL(pdfUrl);
      setPdfUrl(URL.createObjectURL(blob));
      setPreviewTitle(name);
      setPreviewOpen(true);
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : 'Run failed');
    } finally {
      setRunningId(null);
    }
  }, [pdfUrl]);

  const columns = [
    {
      title: 'Name',
      dataIndex: 'name',
      key: 'name',
      sorter: (a: FormScenario, b: FormScenario) => a.name.localeCompare(b.name),
    },
    {
      title: 'Form',
      key: 'form',
      render: (_: unknown, s: FormScenario) => <Tag color="blue">{s.formNumber} {s.editionDate}</Tag>,
    },
    {
      title: 'Fields',
      key: 'fields',
      render: (_: unknown, s: FormScenario) => (
        <Text type="secondary">{Object.keys(s.fieldValues).length} set</Text>
      ),
    },
    {
      title: 'Updated',
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      render: (v: string) => new Date(v).toLocaleDateString(),
      sorter: (a: FormScenario, b: FormScenario) => a.updatedAt.localeCompare(b.updatedAt),
    },
    {
      title: 'Actions',
      key: 'actions',
      render: (_: unknown, s: FormScenario) => (
        <Space>
          <Tooltip title="Run — render filled PDF">
            <Button
              size="small"
              type="primary"
              icon={<PlayCircleOutlined />}
              loading={runningId === s.id}
              onClick={() => handleRun(s.id, s.name)}
            >
              Run
            </Button>
          </Tooltip>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(s)}>Edit</Button>
          <Popconfirm title="Delete this scenario?" onConfirm={() => deleteMutation.mutate(s.id)} okText="Delete" okType="danger">
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
        <Title level={3} style={{ margin: 0 }}>Test Scenarios</Title>
        <Button type="primary" icon={<PlusOutlined />} onClick={openCreate}>New Scenario</Button>
      </div>

      {scenarios.length === 0 && !isLoading ? (
        <Empty
          description="No scenarios yet. Save one from the Convert page or create it here."
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          style={{ marginTop: 48 }}
        >
          <Button type="primary" icon={<PlusOutlined />} onClick={openCreate}>Create Scenario</Button>
        </Empty>
      ) : (
        <Table
          dataSource={scenarios}
          columns={columns}
          rowKey="id"
          loading={isLoading}
          pagination={{ pageSize: 20, showSizeChanger: true, showTotal: (t) => `${t} scenarios` }}
          size="middle"
        />
      )}

      {/* Create / edit modal */}
      <Modal
        title={editScenario ? 'Edit Scenario' : 'New Scenario'}
        open={modalOpen}
        onCancel={() => setModalOpen(false)}
        onOk={handleModalOk}
        confirmLoading={saveMutation.isPending}
        okText="Save"
        width={640}
      >
        <Form layout="vertical" size="small">
          <Form.Item label="Name" required>
            <Input value={formState.name} onChange={(e) => setFormState((p) => ({ ...p, name: e.target.value }))} />
          </Form.Item>
          <Form.Item label="Form Number" required>
            <Input
              value={formState.formNumber}
              onChange={(e) => setFormState((p) => ({ ...p, formNumber: e.target.value.toUpperCase() }))}
              placeholder="e.g. CA2146"
            />
          </Form.Item>
          <Form.Item label="Edition Date" required>
            <Input
              value={formState.editionDate}
              onChange={(e) => setFormState((p) => ({ ...p, editionDate: e.target.value }))}
              placeholder="e.g. 1293"
            />
          </Form.Item>
          <Form.Item label="Field Values">
            <FieldValueEditor
              values={formState.fieldValues}
              onChange={(v) => setFormState((p) => ({ ...p, fieldValues: v }))}
            />
          </Form.Item>
        </Form>
      </Modal>

      {/* PDF preview drawer */}
      <Drawer
        title={`Preview: ${previewTitle}`}
        width="65vw"
        open={previewOpen}
        onClose={() => setPreviewOpen(false)}
        destroyOnClose={false}
      >
        {pdfUrl ? (
          <div style={{ height: 'calc(100vh - 120px)' }}>
            <Worker workerUrl="https://unpkg.com/pdfjs-dist@3.11.174/build/pdf.worker.min.js">
              <Viewer fileUrl={pdfUrl} plugins={[defaultLayoutPluginInstance]} defaultScale={SpecialZoomLevel.PageWidth} />
            </Worker>
          </div>
        ) : <Spin />}
      </Drawer>
    </div>
  );
}

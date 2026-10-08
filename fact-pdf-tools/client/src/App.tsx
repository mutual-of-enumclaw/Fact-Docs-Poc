import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConfigProvider } from 'antd';
import AppLayout from './components/AppLayout';
import CatalogPage from './pages/CatalogPage';
import ConvertPage from './pages/ConvertPage';
import DesignPage from './pages/DesignPage';
import ScenariosPage from './pages/ScenariosPage';

const queryClient = new QueryClient();

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <ConfigProvider theme={{ token: { colorPrimary: '#1677ff' } }}>
        <BrowserRouter>
          <Routes>
            <Route element={<AppLayout />}>
              <Route path="/catalog" element={<CatalogPage />} />
              <Route path="/convert" element={<ConvertPage />} />
              <Route path="/design" element={<DesignPage />} />
              <Route path="/scenarios" element={<ScenariosPage />} />
              <Route path="/" element={<Navigate to="/catalog" replace />} />
            </Route>
          </Routes>
        </BrowserRouter>
      </ConfigProvider>
    </QueryClientProvider>
  );
}

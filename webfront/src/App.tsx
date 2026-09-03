import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { AppStateProvider } from './state/AppState';
import { MetaProvider } from './state/MetaContext';
import AppLayout from './components/AppLayout';
import HomePage from './pages/HomePage';
import ImportPage from './pages/ImportPage';
import DocumentPage from './pages/DocumentPage';
import InboxPage from './pages/InboxPage';
import MasterPage from './pages/MasterPage';
import LogPage from './pages/LogPage';
import AuditLogPage from './pages/AuditLogPage';
import NotFoundPage from './pages/NotFoundPage';

/* Routes mirror the old go(page, module) navigation:
   /                -> home (Overview)
   /import/:module  -> upload / OCR (AP | PODP | II | SO)
   /doc/:docId      -> document editor (header/detail/mapping/SAP post)
   /list[/:module]  -> inbox
   /master, /log, /audit-log */
export default function App() {
  return (
    <AppStateProvider>
      <MetaProvider>
        <BrowserRouter>
          <Routes>
            <Route element={<AppLayout />}>
              <Route index element={<HomePage />} />
              <Route path="import/:module" element={<ImportPage />} />
              <Route path="doc/:docId" element={<DocumentPage />} />
              <Route path="list" element={<InboxPage />} />
              <Route path="list/:module" element={<InboxPage />} />
              <Route path="master" element={<MasterPage />} />
              <Route path="log" element={<LogPage />} />
              <Route path="audit-log" element={<AuditLogPage />} />
              <Route path="404" element={<NotFoundPage />} />
              <Route path="*" element={<Navigate to="/404" replace />} />
            </Route>
          </Routes>
        </BrowserRouter>
      </MetaProvider>
    </AppStateProvider>
  );
}

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles.css';

// 守护器里不需要浏览器自带的右键菜单、拖放和缩放。
window.addEventListener('contextmenu', (event) => {
  if (!(event.target instanceof HTMLInputElement)) event.preventDefault();
});
window.addEventListener('dragover', (event) => event.preventDefault());
window.addEventListener('drop', (event) => event.preventDefault());

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);

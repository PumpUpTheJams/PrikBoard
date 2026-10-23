import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { Tooltips } from './components/Tooltips'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
    <Tooltips />
  </StrictMode>,
)

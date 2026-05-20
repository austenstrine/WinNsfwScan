import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import IsLoadedMessage from './IsLoadedMessage.tsx'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
    <IsLoadedMessage />
  </StrictMode>,
)

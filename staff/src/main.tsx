import { createRoot } from 'react-dom/client';
import { StaffApp } from './app';
import './styles.css';

createRoot(document.getElementById('root')!).render(<StaffApp areas={[]}/>);

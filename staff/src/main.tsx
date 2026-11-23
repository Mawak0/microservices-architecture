import { createRoot } from 'react-dom/client';
import { StaffApp } from './app';
import { observerAreas } from './observer';
import { reviewAreas } from './reviewer';
import './styles.css';

createRoot(document.getElementById('root')!).render(<StaffApp areas={[...observerAreas, ...reviewAreas]}/>);

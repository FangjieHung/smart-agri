export interface ThemeOption {
  id: string;
  label: string;
}

export const texture: ThemeOption[] = [{ id: 'material', label: 'Material' }];

export const COLOR_THEMES: ThemeOption[] = [
  { id: 'verdant', label: 'Verdant 綠' },
  { id: 'midnight', label: 'Midnight 深' },
];

export const DEFAULT_PARADIGM = 'material';
export const DEFAULT_THEME = 'verdant';

export const PARADIGM_KEY = 'sa.paradigm';
export const THEME_KEY = 'sa.theme';

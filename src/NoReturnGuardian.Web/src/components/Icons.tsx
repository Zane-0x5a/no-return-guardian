// 线性图标：24 盒、1.3 线宽、圆头。只画必要的笔画，和细线控件同一重量。
import type { SVGProps } from 'react';

const base: SVGProps<SVGSVGElement> = {
  width: 18,
  height: 18,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.3,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  'aria-hidden': true,
};

export const Icon = {
  /** 守护器的标志：一道细线拱门，脚下一扇实心小门（与应用图标同一图形，scripts/make-icons.py）。
   *  门洞读起来像“再进去一次”，按钮上只给“重开战斗”用。 */
  mark: () => (
    <svg {...base}>
      <path d="M7.6 20.5V9.1a4.4 4.4 0 0 1 8.8 0v11.4" />
      <path d="M10.5 20.5v-3.2a1.5 1.5 0 0 1 3 0v3.2Z" fill="currentColor" stroke="none" />
    </svg>
  ),
  /** 保护战备：存档点的小旗。 */
  flag: () => (
    <svg {...base}>
      <path d="M6.5 20.5V4" />
      <path d="M6.5 4.8h10.8l-2.6 3.8 2.6 3.8H6.5" />
    </svg>
  ),
  restore: () => (
    <svg {...base}>
      <path d="M4.5 12a7.5 7.5 0 1 0 2.2-5.3" />
      <path d="M4.5 4.5v3.4h3.4" />
    </svg>
  ),
  undo: () => (
    <svg {...base}>
      <path d="M9 5.5 5 9.5l4 4" />
      <path d="M5.5 9.5H14a4.5 4.5 0 0 1 0 9h-2" />
    </svg>
  ),
  play: () => (
    <svg {...base}>
      <path d="M8 5.8v12.4L18 12 8 5.8Z" />
    </svg>
  ),
  trash: () => (
    <svg {...base}>
      <path d="M5 7h14" />
      <path d="M9.5 7V5.2h5V7" />
      <path d="M7 7l.8 12h8.4L17 7" />
    </svg>
  ),
  /** 设置：两根滑轨。设置页里都是开关和取值，不用齿轮——细线画的齿轮在小尺寸下像太阳。 */
  settings: () => (
    <svg {...base}>
      <path d="M4 8.5h9M17.4 8.5H20M4 15.5h2.6M11 15.5h9" />
      <circle cx="15.2" cy="8.5" r="2.2" />
      <circle cx="8.8" cy="15.5" r="2.2" />
    </svg>
  ),
  minus: () => (
    <svg {...base} width={14} height={14}>
      <path d="M5 12h14" />
    </svg>
  ),
  close: () => (
    <svg {...base} width={14} height={14}>
      <path d="M6 6l12 12M18 6 6 18" />
    </svg>
  ),
  back: () => (
    <svg {...base}>
      <path d="M14.5 6 8.5 12l6 6" />
    </svg>
  ),
  folder: () => (
    <svg {...base} width={16} height={16}>
      <path d="M3.8 7.2V18h16.4V8.8h-8.1L10.4 7H3.8Z" />
    </svg>
  ),
};

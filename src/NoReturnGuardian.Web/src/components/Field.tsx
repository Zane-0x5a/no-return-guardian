import { Warp } from '@paper-design/shaders-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { Mode } from '../types';

// 主题色场：铺满整个窗口、缓慢流动的多色大理石纹形体（Paper Shaders · Warp）。
// 色板就是守护状态——守护中是酒红、灰玫瑰与暗紫，观察中退成灰紫，待机沉成夜蓝并放慢，
// 征途疑似结束时烧成绯红与余烬；原生恢复进行中保持当前色板，只是流得快一些。切换时约 1.8 秒连续过渡。
// 每个色板首尾都是暗底，把大面积拉回暗色，亮色只在纹路里流过，文字始终压得住。

type Palette = [string, string, string, string, string, string];

interface Look {
  colors: Palette;
  speed: number;
}

const looks: Record<Mode, Look> = {
  guarding: { colors: ['#0a0709', '#4a1020', '#80625a', '#2a1436', '#5c2230', '#0c080b'], speed: 0.3 },
  observing: { colors: ['#09080a', '#2c2a38', '#6a5d63', '#33263b', '#44343e', '#0a090b'], speed: 0.24 },
  idle: { colors: ['#06070a', '#142039', '#36465f', '#211b34', '#1f2c45', '#07080b'], speed: 0.12 },
  alert: { colors: ['#0a0506', '#6e1416', '#b8492a', '#3a0b1b', '#8c2a1c', '#0c0607'], speed: 0.34 },
};

type Rgb = [number, number, number];

function parse(hex: string): Rgb {
  const value = hex.replace('#', '');
  return [0, 2, 4].map((index) => parseInt(value.slice(index, index + 2), 16)) as Rgb;
}

function format(rgb: Rgb): string {
  return '#' + rgb.map((n) => Math.round(Math.min(255, Math.max(0, n))).toString(16).padStart(2, '0')).join('');
}

function mix(from: Look, to: Look, t: number): Look {
  return {
    colors: from.colors.map((color, index) => {
      const a = parse(color);
      const b = parse(to.colors[index]);
      return format([0, 1, 2].map((i) => a[i] + (b[i] - a[i]) * t) as Rgb);
    }) as Palette,
    speed: from.speed + (to.speed - from.speed) * t,
  };
}

const ease = (t: number) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);

/**
 * 色场的时钟。色场流得很慢，不需要每秒 60 帧：窗口在前台时 30 帧，失焦时 12 帧，页面隐藏时停住。
 * Paper 的 speed 设为 0，由这里推进 frame（毫秒），每一帧只在这里决定的时刻重绘。
 */
function useFieldClock(speed: number): number {
  const [frame, setFrame] = useState(0);
  const rate = useRef(speed);
  rate.current = speed;
  useEffect(() => {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    let elapsed = 0;
    let last = performance.now();
    let timer = 0;
    const tick = () => {
      const now = performance.now();
      if (document.visibilityState === 'visible') {
        elapsed += (now - last) * rate.current;
        setFrame(elapsed);
      }
      last = now;
      timer = window.setTimeout(tick, document.hasFocus() ? 1000 / 30 : 1000 / 12);
    };
    timer = window.setTimeout(tick, 1000 / 30);
    return () => window.clearTimeout(timer);
  }, []);
  return frame;
}

function useLook(target: Look, duration = 1800): Look {
  const [look, setLook] = useState(target);
  const current = useRef(target);
  useEffect(() => {
    const from = current.current;
    if (from === target) return;
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const start = performance.now();
    let frame = 0;
    const step = (now: number) => {
      const t = reduced ? 1 : Math.min(1, (now - start) / duration);
      const next = t >= 1 ? target : mix(from, target, ease(t));
      current.current = next;
      setLook(next);
      if (t < 1) frame = requestAnimationFrame(step);
    };
    frame = requestAnimationFrame(step);
    return () => cancelAnimationFrame(frame);
  }, [target, duration]);
  return look;
}

export function Field({ mode, working }: { mode: Mode; working: boolean }) {
  const target = useMemo(() => (working ? { ...looks[mode], speed: looks[mode].speed + 0.3 } : looks[mode]), [mode, working]);
  const look = useLook(target);
  const frame = useFieldClock(look.speed);

  // 色场柔和，不需要高分辨率；限制像素数让它在高 DPI 屏上也只占很少的 GPU。
  return (
    <div className="field" aria-hidden>
      <Warp
        className="field-shader"
        colors={look.colors}
        proportion={0.4}
        softness={1}
        distortion={0.2}
        swirl={0.78}
        swirlIterations={7}
        shape="edge"
        shapeScale={0.07}
        rotation={22}
        scale={1.15}
        speed={0}
        frame={frame}
        minPixelRatio={1}
        maxPixelCount={800_000}
      />
      <div className="field-grain" />
      <div className="field-scrim" />
    </div>
  );
}

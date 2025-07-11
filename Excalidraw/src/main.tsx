import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import "@excalidraw/excalidraw/index.css";
import type * as TExcalidraw from "@excalidraw/excalidraw";
import type { ExcalidrawImperativeAPI } from '@excalidraw/excalidraw/types';

import App from './App.tsx'


declare global {
  interface Window {
    ExcalidrawLib: typeof TExcalidraw;
  }
}


const { Excalidraw } = window.ExcalidrawLib;

export interface RenderProps {
  height: number,
  elementId: string,
  jsonData?: string,
  isDarkMode?: boolean,
  onChange?: () => void,
  onClose?: () => void,
}

export function renderExcalidraw({ height, elementId, jsonData, isDarkMode, onChange, onClose }: RenderProps) {
  let excalidrawApi: ExcalidrawImperativeAPI | null = null;

  const { exportToBlob, serializeAsJSON } = window.ExcalidrawLib;

  const exportPngBlob = async (darkMode?: boolean) => {
    if (excalidrawApi != null) {
      return await exportToBlob({
        elements: excalidrawApi.getSceneElements(),
        appState: {
          ...excalidrawApi.getAppState(),
          exportWithDarkMode: darkMode ?? isDarkMode ?? false,
          exportEmbedScene: true,
        },
        files: excalidrawApi.getFiles(),
        type: 'png',
      });
    }
  };

  const exportJson = () => {
    if (excalidrawApi != null) {
      return serializeAsJSON(
        excalidrawApi.getSceneElements(),
        excalidrawApi.getAppState(),
        excalidrawApi.getFiles(),
        "local"
      );
    }
  };

  const exalidrawAutoCacheKey = elementId + "-excalidraw-state"
  const autoSaveId = setInterval(() => {
    if (excalidrawApi != null) {
      const data = exportJson();
      if (data) {
        localStorage.setItem(exalidrawAutoCacheKey, data);
        console.log("Auto saved Excalidraw state");
      }
    }
  }, 10000);

  createRoot(document.getElementById(elementId)!).render(
    <StrictMode>
      <App
        height={height}
        jsonData={localStorage.getItem(exalidrawAutoCacheKey) ?? jsonData}
        isDarkMode={isDarkMode}
        onClose={() => {
          console.log("Excalidraw is closing");
          clearInterval(autoSaveId);
          localStorage.removeItem(exalidrawAutoCacheKey);
          onClose?.();
        }}
        useCustom={(api: any, args?: any[]) => { }}
        setExcalidrawApi={x => {
          excalidrawApi = x;
          excalidrawApi.onChange(_ => {
            onChange?.();
          });
        }}
        excalidrawLib={window.ExcalidrawLib}
      >
        <Excalidraw />
      </App>
    </StrictMode>,
  )

  return {
    exportPngBlob,
    exportJson,
  }
}


window["renderExcalidraw"] = renderExcalidraw;

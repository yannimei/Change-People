using System;
using System.IO;
using UnityEngine;

namespace SimpleWebRTC {
    // Loaded from <Application.persistentDataPath>/config.json on device.
    // On Quest: /sdcard/Android/data/com.BlackWhaleStudio.UnityQuestVisionKit/files/config.json
    // Push with: adb push config.json /sdcard/Android/data/com.BlackWhaleStudio.UnityQuestVisionKit/files/
    [Serializable]
    public class DecartConfig {
        public const string FileName = "config.json";
        public const string BaseUrl = "wss://api3.decart.ai/v1/stream";

        public string apiKey;
        public string mirageModel;
        public string lucyModel;

        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        public static DecartConfig Load() {
            var path = FilePath;
            if (!File.Exists(path)) {
                Debug.LogWarning($"[DecartConfig] No config file at {path}. Falling back to inspector values.");
                return null;
            }

            try {
                var json = File.ReadAllText(path);
                var cfg = JsonUtility.FromJson<DecartConfig>(json);
                if (cfg == null || string.IsNullOrEmpty(cfg.apiKey)) {
                    Debug.LogWarning($"[DecartConfig] Config at {path} is missing apiKey. Falling back to inspector values.");
                    return null;
                }
                Debug.Log($"[DecartConfig] Loaded config from {path}");
                return cfg;
            } catch (Exception e) {
                Debug.LogError($"[DecartConfig] Failed to parse {path}: {e.Message}");
                return null;
            }
        }

        public string BuildUrl(string modelName) {
            if (string.IsNullOrEmpty(modelName)) {
                Debug.LogError("[DecartConfig] Model name is empty.");
                return null;
            }
            return $"{BaseUrl}?api_key={apiKey}&model={modelName}";
        }
    }
}

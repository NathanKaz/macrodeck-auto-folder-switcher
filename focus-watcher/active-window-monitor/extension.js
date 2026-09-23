import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';

const FILE_NAME = 'macrodeck-focus.json';
const HISTORY_FILE = 'macrodeck-focus.history.jsonl';
const HISTORY_MAX = 500;

// Window types that are never a "real" application window.
const IGNORED_TYPES = new Set([
    Meta.WindowType.DESKTOP,
    Meta.WindowType.DOCK,
]);

let _signalId = 0;
let _extension = null;

function runtimeDir() {
    const dir = GLib.get_user_runtime_dir();
    return dir || '/tmp';
}

function writeFile(text) {
    const path = GLib.build_filenamev([runtimeDir(), FILE_NAME]);
    try {
        const file = Gio.File.new_for_path(path);
        const ok = file.replace_contents(
            text,
            null,
            false,
            Gio.FileCreateFlags.REPLACE_DESTINATION,
            null
        );
        void ok;
    } catch (e) {
        log(`AWM: failed to write ${path}: ${e}`);
    }
}

function windowInfo(window) {
    if (!window) {
        return {
            version: 1,
            app_id: null,
            wm_class: null,
            wm_class_instance: null,
            title: null,
            pid: null,
            time: Date.now() / 1000,
        };
    }

    const type = window.get_window_type();
    if (IGNORED_TYPES.has(type)) {
        return null;
    }

    let wmClass = null;
    let wmClassInstance = null;
    try {
        wmClass = window.get_wm_class?.() ?? null;
        wmClassInstance = window.get_wm_class_instance?.() ?? null;
    } catch (e) {
        log(`AWM: wm_class lookup failed: ${e}`);
    }

    let appId = null;
    try {
        const app = window.get_app?.();
        if (app) {
            appId = app.get_id?.() ?? null;
        }
    } catch (e) {
        log(`AWM: app lookup failed: ${e}`);
    }

    // Some windows have no matching app (WM_CLASS outlives the .desktop id).
    if (!appId && wmClass) {
        appId = wmClass;
    }

    let title = null;
    try {
        title = window.get_title?.() ?? null;
    } catch (e) {
        log(`AWM: title lookup failed: ${e}`);
    }

    let pid = null;
    try {
        pid = window.get_pid?.() ?? null;
    } catch (e) {
        log(`AWM: pid lookup failed: ${e}`);
    }

    return {
        version: 1,
        app_id: appId,
        wm_class: wmClass,
        wm_class_instance: wmClassInstance,
        title: title,
        pid: pid,
        time: Date.now() / 1000,
    };
}

function appendHistory(line) {
    const path = GLib.build_filenamev([runtimeDir(), HISTORY_FILE]);
    try {
        const file = Gio.File.new_for_path(path);
        let lines = [];
        try {
            const [ok, contents] = file.load_contents(null);
            if (ok) {
                const text = new TextDecoder().decode(contents);
                lines = text.split('\n').filter((l) => l.length > 0);
            }
        } catch (e) {
            // First entry ever: no history on disk yet.
        }
        lines.push(line);
        if (lines.length > HISTORY_MAX) {
            lines = lines.slice(lines.length - HISTORY_MAX);
        }
        file.replace_contents(
            lines.join('\n') + '\n',
            null,
            false,
            Gio.FileCreateFlags.REPLACE_DESTINATION,
            null
        );
    } catch (e) {
        log(`AWM: failed to append history ${path}: ${e}`);
    }
}

function publish() {
    const window = global.display.get_focus_window();
    const info = windowInfo(window);
    const record = info ?? {
        version: 1,
        app_id: null,
        wm_class: null,
        wm_class_instance: null,
        title: null,
        pid: null,
        time: Date.now() / 1000,
        reason: 'ignored',
    };
    const text = JSON.stringify(record, null, 2);
    writeFile(text);
    appendHistory(JSON.stringify(record));
}

export default class ActiveWindowMonitorExtension {
    enable() {
        _extension = this;
        // Fires on every focus change, including inside alt-tab, workspaces etc.
        _signalId = global.display.connect('notify::focus-window', publish);
        publish();
    }

    disable() {
        if (_signalId !== 0) {
            global.display.disconnect(_signalId);
            _signalId = 0;
        }
        _extension = null;
    }
}
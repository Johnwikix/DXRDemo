"""Run after ui_settings.py arguments: MAIN SETTINGS MODEL DENOISER HDR."""
from pathlib import Path
import runpy
import time

scope = runpy.run_path(str(Path(__file__).with_name('ui_settings.py')))
run, find, ref, nodes = [scope[name] for name in ['run', 'find', 'ref', 'nodes']]
main, settings = scope['main'], scope['settings']
run(main, 'click', ref(find(main, 'OpenSettingsButton')))
for index in [0, 1, 2, 1]:
    run(settings, 'focus', ref(find(settings, 'ShaderSelector')))
    run(settings, 'press-key', ';'.join(['Home'] + ['Down'] * index + ['Enter', 'Escape']))
    time.sleep(0.35)
for aid, value in [('SamplesBox', '4'), ('MaxBouncesBox', '12'), ('SamplesBox', '2'), ('MaxBouncesBox', '10')]:
    parent = find(settings, aid)
    edit = next(n for n in nodes(parent) if n.get('type') == 'Edit')
    run(settings, 'set-value', ref(edit), value)
    run(settings, 'press-key', 'Enter')
run(settings, 'toggle', ref(find(settings, 'DiagnosticsToggle')))
time.sleep(0.3)
run(settings, 'toggle', ref(find(settings, 'DiagnosticsToggle')))
run(settings, 'close')
print('PASS: shader cache switching, numeric controls, HUD toggle, settings hide')

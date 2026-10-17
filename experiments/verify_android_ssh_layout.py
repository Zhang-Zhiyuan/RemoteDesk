"""Explicitly owned AVD: long-name SSH login, real window/IME, no network connection."""
import argparse, json, pathlib, re, shlex, subprocess, time, xml.etree.ElementTree as ET

p=argparse.ArgumentParser();p.add_argument('--output',required=True);p.add_argument('--before',action='store_true')
p.add_argument('--avd-name',choices=('RemoteDeskRows_20261004_r1','RemoteDeskRelaySetup_20260911'),default='RemoteDeskRows_20261004_r1')
p.add_argument('--font-scale',type=float,choices=(1.0,1.5,1.8,2.0));a=p.parse_args()
out=pathlib.Path(a.output);out.mkdir(parents=True,exist_ok=False)
adb=pathlib.Path.home()/'AppData/Local/Android/Sdk/platform-tools/adb.exe'
package='com.remotedesk.viewerprobe';serial='emulator-5586';checks=[]
def run(*args):return subprocess.run([str(adb),'-s',serial,*map(str,args)],check=True,capture_output=True,timeout=30).stdout
def shell(*args):return run('shell',' '.join(shlex.quote(str(x)) for x in args)).decode('utf-8','replace').strip()
assert a.avd_name in run('emu','avd','name').decode().splitlines()
original_font_scale=shell('settings','get','system','font_scale')
def state():return json.loads(shell('run-as',package,'cat','files/ssh-state.json'))
def wait(predicate,timeout=15):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        try:
            value=state()
            if predicate(value):return value
        except (ValueError,subprocess.CalledProcessError):pass
        time.sleep(.2)
    raise AssertionError('SSH UI condition not reached')
def capture(name):
    (out/(name+'.png')).write_bytes(run('exec-out','screencap','-p'))
    (out/(name+'.json')).write_text(json.dumps(state(),ensure_ascii=False,indent=2),encoding='utf-8')
def check(name,value):
    checks.append(dict(name=name,passed=bool(value)));assert value,name

def dpi(action,text=None):
    args=['am','broadcast','-n',package+'/com.remotedesk.agent.DpiProbeReceiver','--es','action',action,'--es','runId',action]
    if text is not None:args+=['--es','text',text]
    shell(*args);time.sleep(.8)
    return json.loads(shell('run-as',package,'cat','files/dpi-ui.json'))
def tap(text):
    shell('uiautomator','dump','/data/local/tmp/owned-ssh-layout.xml')
    xml=shell('cat','/data/local/tmp/owned-ssh-layout.xml');(out/'last-ui.xml').write_text(xml,encoding='utf-8')
    nodes=[n for n in ET.fromstring(xml).iter('node') if n.get('text')==text]
    assert len(nodes)==1,'Expected visible control '+text
    dpi('activate',text)
report=dict(scope='Owned AVD isolated product SSH Activity; real layout and IME, in-app control activation; UI-only port1, no network session',checks=checks,passed=False)
try:
    if a.font_scale is not None:
        shell('settings','put','system','font_scale',a.font_scale)
        wait(lambda s:s['pageReady'] and s['config'].startswith('{'+str(a.font_scale)+' '))
    s=wait(lambda s:s['pageReady']);capture('login-long-name')
    check('explicit_long_device_name_fixture',len(s['status'])>=100)
    height=s['loginBounds'][3]-s['loginBounds'][1]
    if a.before:check('reproduced_fixed_status_starves_long_name_login',height<100)
    else:
        check('long_name_login_retains_scrollable_space',height>=120 and height>s['statusBounds'][3]-s['statusBounds'][1])
        # Exercise the real product callback and native scrollable status dialog.
        dpi('activate',s['status']);time.sleep(.4)
        details=dpi('snapshot');capture('status-details')
        check('full_long_status_remains_available_in_dialog',len(details['windows']) > 1 and any(v.get('text')==s['status'] for w in details['windows'] for v in w['views']))
        tap('关闭')
        for label in ('目标电脑的系统账号','目标 SSH 密码（不保存）'):
            detail=dpi('focus',label);time.sleep(.8);detail=dpi('snapshot');capture('ime-'+('username' if '账号' in label else 'password'))
            edit=next(v for w in detail['windows'] for v in w['views'] if v.get('hint')==label)
            check(label+' reachable with real IME',edit['onScreen'] and edit['contentHeight']>=edit['textHeight'] and 'mInputShown=true' in shell('dumpsys','input_method'))
            shell('input','keyevent','KEYCODE_BACK')
        detail=dpi('reveal','连接终端');capture('connect-reachable')
        button=next(v for w in detail['windows'] for v in w['views'] if v.get('text')=='连接终端')
        check('connect_button_fully_reachable_after_scroll',button['onScreen'] and button['contentHeight']>=button['textHeight'])
        dpi('reveal','更多设置 ▸');tap('更多设置 ▸')
        detail=dpi('reveal','查看 / 管理主机身份');capture('advanced-settings')
        button=next(v for w in detail['windows'] for v in w['views'] if v.get('text')=='连接终端')
        check('connect_remains_visible_with_advanced_settings',button['onScreen'] and button['contentHeight']>=button['textHeight'])
        tap('连接终端');s=wait(lambda s:'填写' in s['status'] or '密钥' in s['status'])
        check('empty_credentials_do_not_open_network_session',not s['connection'])
    report['passed']=True
finally:
    if a.font_scale is not None:
        if original_font_scale=='null':shell('settings','delete','system','font_scale')
        else:shell('settings','put','system','font_scale',original_font_scale)
    (out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')

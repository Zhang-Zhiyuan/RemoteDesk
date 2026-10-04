"""Owned emulator + isolated package only. Reads no secrets and never logs credentials."""
import argparse, json, pathlib, shlex, subprocess, time
import re, xml.etree.ElementTree as ET

p=argparse.ArgumentParser();p.add_argument('--output',required=True);p.add_argument('--serial',default='emulator-5586');p.add_argument('--phase',default='basic');a=p.parse_args()
if a.serial!='emulator-5586': raise SystemExit('Only the task-owned AVD is allowed')
out=pathlib.Path(a.output);out.mkdir(parents=True,exist_ok=True)
adb=pathlib.Path.home()/'AppData/Local/Android/Sdk/platform-tools/adb.exe'
package='com.remotedesk.viewerprobe';checks=[]
def run(*args): return subprocess.run([str(adb),'-s',a.serial,*args],stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True).stdout
def shell(*args): return run('shell',' '.join(shlex.quote(str(x)) for x in args)).decode('utf-8','replace')
def command(action,text=None):
    args=['am','broadcast','-n',package+'/com.remotedesk.agent.SshProbeReceiver','--es','action',action]
    if text is not None: args+=['--es','text',text]
    shell(*args)
def state():
    data=json.loads(shell('run-as',package,'cat','files/ssh-state.json'))
    data['text']=json.loads(data.get('terminalDom','""')) or ''
    return data
def wait(predicate,timeout=20):
    end=time.monotonic()+timeout;last={}
    while time.monotonic()<end:
        try:
            last=state()
            if predicate(last):return last
        except (ValueError,subprocess.CalledProcessError):pass
        time.sleep(.3)
    raise AssertionError('Timed out: '+str({k:last.get(k) for k in ('status','pageReady','connection','trustDialog','failure','text')}))
def check(name,value=True):
    assert value,name
    checks.append({'name':name,'passed':True});print(name,flush=True)
def click_label(text, attribute='text'):
    shell('uiautomator','dump','/data/local/tmp/owned-ssh-ui.xml')
    xml=shell('cat','/data/local/tmp/owned-ssh-ui.xml')
    (out/'last-ui.xml').write_text(xml,encoding='utf-8')
    nodes=[n for n in ET.fromstring(xml).iter('node') if n.get(attribute)==text]
    assert nodes,'Missing system-visible control '+text
    bounds=list(map(int,re.findall(r'\d+',nodes[-1].get('bounds'))))
    x,y=(bounds[0]+bounds[2])//2,(bounds[1]+bounds[3])//2
    if nodes[-1].get('package')=='com.android.documentsui':
        apk=shell('pm','path',package).strip().removeprefix('package:')
        shell('env','CLASSPATH='+apk,'app_process','/system/bin','com.remotedesk.agent.SafTouchProbe',x,y,80)
    else:shell('input','tap',x,y)
def capture(name):
    data=state();(out/(name+'.json')).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    png=run('exec-out','screencap','-p')
    if not png.startswith(b'\x89PNG'): raise AssertionError('Screenshot missing/blocked; do not count visual verification')
    (out/(name+'.png')).write_bytes(png)
    return data
report={'phase':a.phase,'serial':a.serial,'package':package,'checks':checks,'completed':False}
try:
    if a.phase in ('key-race-before', 'key-race-fixed'):
        wait(lambda s:s['pageReady']);command('selectKey','ssh-key-slow')
        wait(lambda s:'正在读取' in s['status']);command('key','改用密码登录')
        time.sleep(4)
        result=capture('late-key-read')
        if a.phase=='key-race-before':
            check('reproduced_late_key_read_overrides_explicit_password_mode',result['privateKeySelected'])
        else:
            check('late_key_read_cannot_override_explicit_password_mode',not result['privateKeySelected'] and '目标 SSH 密码' in result['passwordHint'])
            command('selectKey','ssh-key-slow');wait(lambda s:'正在读取' in s['status'])
            shell('input','keyevent','KEYCODE_HOME');time.sleep(4)
            result=state();check('background_invalidates_inflight_private_key_read',not result['privateKeySelected'])
    elif a.phase=='renderer-error':
        wait(lambda s:s['pageReady']);command('stopRenderer')
        s=wait(lambda s:'显示进程已退出' in s['status'] and not s['pageReady'])
        capture('renderer-error')
        check('login_renderer_exit_disables_connect_with_visible_error',not s['connection'] and not s['connectEnabled'])
        check('renderer_exit_does_not_crash_isolated_app',bool(shell('pidof',package).strip()))
    elif a.phase=='saf-key':
        # Real Android DocumentsUI picker, not injected onActivityResult or reflected key bytes.
        wait(lambda s:s['pageReady']);command('key','选择 SSH 私钥（可选）');time.sleep(1)
        try:click_label('List view','content-desc')
        except AssertionError:pass
        capture('system-privatekey-picker')
        click_label('saf-ssh-key.bin');time.sleep(1)
        wait(lambda s:s['privateKeySelected']);capture('system-key-selected')
        check('system_saf_selection_returns_private_key_via_product_content_resolver')
        command('connectSelected');wait(lambda s:s['trustDialog']);capture('system-key-host-identity')
        command('trust');wait(lambda s:s['status'].startswith('SSH 已连接'),25)
        command('input','echo SAF-ED25519-$((27+27)); tty\r')
        wait(lambda s:'SAF-ED25519-54' in s['text'] and '/dev/pts/' in s['text'])
        capture('system-key-real-pty');check('system_selected_openssh_ed25519_key_authenticates_real_linux_pty')
    elif a.phase=='basic':
        wait(lambda s:s['pageReady']);check('local_xterm_loaded_on_webview_83')
        capture('login');command('connect');wait(lambda s:s['trustDialog']);capture('first-hostkey')
        check('first_host_key_requires_explicit_confirmation')
        time.sleep(25)
        wait(lambda s:s['trustDialog'] and s['connection'],3);check('trust_dialog_survives_20_second_gate_deadline')
        command('trust');s=wait(lambda s:s['status'].startswith('SSH 已连接'),25)
        check('real_linux_ssh_public_key_auth_and_shell_connected');check('password_field_cleared',s['passwordLength']==0)
        command('input',"printf '\\033[2J\\033[H\\033[32mANDROID-SSH 中文测试😀\\033[0m\\n'; tty; stty size\r")
        s=wait(lambda s:'ANDROID-SSH 中文测试😀' in s['text'] and '/dev/pts/' in s['text'])
        capture('real-pty-unicode');check('real_linux_pty_chinese_emoji_ansi_output')
    elif a.phase=='tui':
        wait(lambda s:s['status'].startswith('SSH 已连接'))
        command('input',"printf '\\033[?1049h\\033[2J\\033[3;5H\\033[31mTUI-CELL-中文\\033[0m'; sleep 20\r")
        wait(lambda s:'TUI-CELL-中文' in s['text']);capture('alternate-screen');check('ansi_alternate_buffer_cursor_addressing_and_color')
        command('key','Ctrl')
        wait(lambda s:'✓' in s.get('ctrl',''),3)
        shell('input','text','c')
        time.sleep(1)
        command('input',"printf '\\033[?1049l'; echo CTRL-C-$((21+21))\r")
        wait(lambda s:'CTRL-C-42' in s['text'],8);check('ctrl_c_interrupts_remote_sleep')
        shell('settings','put','system','accelerometer_rotation','0');shell('settings','put','system','user_rotation','1')
        time.sleep(2);command('input','echo RESIZE-$((22+22)); stty size\r')
        s=wait(lambda s:'RESIZE-44' in s['text']);capture('landscape-resize');check('rotation_preserves_session_and_resizes_pty',s['connection'])
        shell('input','keyevent','KEYCODE_HOME');wait(lambda s:not s['connection'])
        capture('background-closed');check('background_disconnect_discards_pending_input')
    elif a.phase=='extras':
        wait(lambda s:s['status'].startswith('SSH 已连接'))
        command('ime','echo IME-中文-$((23+23))');time.sleep(.5);command('input','\r')
        wait(lambda s:'IME-中文-46' in s['text']);check('android_input_connection_commits_chinese_to_remote_shell')
        command('clipboard','OWNED-LOCAL-CLIPBOARD')
        command('input',"printf '\\033]52;c;UkVNT1RFLUNMSVBCT0FSRC1OTy1VU0VS\\007'; echo OSC-$((25+25))\r")
        wait(lambda s:'OSC-50' in s['text']);command('clipboardRead')
        wait(lambda s:s['clipboardText']=='OWNED-LOCAL-CLIPBOARD');check('remote_osc52_cannot_change_android_clipboard')
        command('key','复制');time.sleep(.5);capture('copy-dialog');click_label('复制全部可见文字');command('clipboardRead')
        wait(lambda s:'IME-中文-46' in s['clipboardText']);check('explicit_native_copy_dialog_copies_visible_terminal')
        command('clipboard','echo PASTE-$((24+24))\r');command('key','粘贴');time.sleep(.5);capture('paste-confirmation')
        click_label('取消');time.sleep(.5);check('clipboard_paste_cancel_sends_nothing','PASTE-48' not in state()['text'])
        command('key','粘贴');click_label('确认粘贴');time.sleep(.5);command('input','\r')
        wait(lambda s:'PASTE-48' in s['text']);capture('ime-clipboard');check('confirmed_clipboard_paste_reaches_shell')
    elif a.phase=='changed-key':
        command('changeTrust');command('connect')
        s=wait(lambda s:'密钥已变化' in s['status']);capture('changed-key-rejected')
        check('changed_host_key_hard_rejected_without_overwrite',not s['connection'] and not s['trustDialog'])
    elif a.phase=='forget-key':
        command('key','SSH 主机身份');click_label('忘记此身份');capture('forget-second-confirmation');click_label('取消')
        command('connect');wait(lambda s:'密钥已变化' in s['status']);check('cancel_forget_retains_rejected_identity')
        command('key','SSH 主机身份');click_label('忘记此身份');click_label('已核实，忘记')
        wait(lambda s:'身份已忘记' in s['status']);check('explicit_forget_scoped_to_target_ssh_identity')
        command('connect');wait(lambda s:s['trustDialog']);capture('fresh-confirmation-after-forget')
        command('reject');wait(lambda s:not s['connection']);check('forgotten_identity_still_requires_confirmation_and_reject_cancels')
    report['completed']=True
except Exception as ex:
    report['failure']=str(ex)
    try:capture('failure')
    except Exception:pass
    raise
finally:
    report['passed']=report['completed'] and all(c['passed'] for c in checks)
    (out/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')

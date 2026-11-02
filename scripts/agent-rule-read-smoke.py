# SPDX-License-Identifier: GPL-3.0-or-later
"""Validate only newly added rule read/history tools; reuse other accepted MCP checks."""
from pathlib import Path
import json,os,subprocess,uuid,queue,threading
root=Path(__file__).resolve().parent.parent
out=root/'artifacts/agent-read'/uuid.uuid4().hex;out.mkdir(parents=True)
env=os.environ.copy();env.update(SYSBOX_TEST_RULE_DIRECTORY=str(out/'rules'),SYSBOX_TEST_INDEX_DIRECTORY=str(out/'index'),SYSBOX_TEST_PIPE_SUFFIX=uuid.uuid4().hex)
cli=root/'src/SysBox.Cli/bin/Debug/net10.0-windows/sysbox.exe';mcp=root/'src/SysBox.Mcp/bin/Debug/net10.0-windows/SysBox.Mcp.exe'
host_pid=0;process=None
try:
    status=subprocess.run([str(cli),'status','--json'],env=env,capture_output=True,text=True,encoding='utf-8',check=True,timeout=20)
    host_pid=json.loads(status.stdout)['data']['processId']
    active=subprocess.run([str(cli),'rules','read','--json'],env=env,capture_output=True,text=True,encoding='utf-8',check=True,timeout=20)
    cli_rule=json.loads(active.stdout)['data'];assert cli_rule['revision']==1 and 'schema_version' in cli_rule['yaml']
    with open(out/'stderr.txt','w',encoding='utf-8') as errors:
        process=subprocess.Popen([str(mcp)],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=errors,text=True,encoding='utf-8',bufsize=1)
        messages=queue.Queue()
        def read():
            for line in process.stdout:messages.put(json.loads(line))
        threading.Thread(target=read,daemon=True).start()
        def send(value):process.stdin.write(json.dumps(value)+'\n');process.stdin.flush()
        def request(identifier,method,params):
            send({'jsonrpc':'2.0','id':identifier,'method':method,'params':params})
            while True:
                value=messages.get(timeout=20)
                if value.get('id')==identifier:
                    assert 'error' not in value,value
                    return value['result']
        request(1,'initialize',{'protocolVersion':'2025-06-18','capabilities':{},'clientInfo':{'name':'new-rule-read-fixture','version':'1'}})
        send({'jsonrpc':'2.0','method':'notifications/initialized'})
        names=[tool['name'] for tool in request(2,'tools/list',{})['tools']];assert len(names)==12,names
        result=request(3,'tools/call',{'name':'rules_read','arguments':{}});assert not result.get('isError'),result
        active=json.loads(result['content'][0]['text']);assert active['sha256']==cli_rule['sha256'] and active['revision']==1
        result=request(4,'tools/call',{'name':'rules_history','arguments':{}});assert not result.get('isError'),result
        history=json.loads(result['content'][0]['text']);assert history['activeRevision']==1 and len(history['versions'])==1 and history['pending']==[]
        report={'checks':['CLI rules read and MCP rules_read return matching active YAML revision/hash','New MCP rules_history returns bounded metadata without mutation'],'toolCount':len(names)}
        (out/'rule-read-flow.json').write_text(json.dumps(report,indent=2),encoding='utf-8');print(json.dumps(report))
finally:
    if process:
        process.stdin.close()
        try:process.wait(timeout=5)
        except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=5)
    if host_pid:subprocess.run(['taskkill.exe','/PID',str(host_pid),'/F'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)

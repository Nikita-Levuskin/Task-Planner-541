'use strict';
const $=id=>document.getElementById(id);
let accessToken=null, currentUser=null, refreshPromise=null, registering=false, editing=null, sharing=null, rows=[], polling=false;
let seenNotifications=new Set(), inboxLoaded=false;
function element(tag,text,cls){const x=document.createElement(tag);x.textContent=text;if(cls)x.className=cls;return x;}
function button(text,action,cls='secondary'){const b=element('button',text,cls);b.type='button';b.addEventListener('click',()=>Promise.resolve().then(action).catch(error=>$('status').textContent=error.message));return b;}
async function refresh(){
  if(!refreshPromise) refreshPromise=fetch('/api/auth/refresh',{method:'POST',headers:{'X-Planner-Request':'1'}})
    .then(async r=>{if(!r.ok)throw new Error('Войдите в аккаунт');const body=await r.json();accessToken=body.accessToken;currentUser=body.user;return body;})
    .finally(()=>refreshPromise=null);
  return refreshPromise;
}
async function api(path,options={},retry=true){
  const headers={'Content-Type':'application/json','X-Planner-Request':'1',...options.headers};
  if(accessToken)headers.Authorization=`Bearer ${accessToken}`;
  const r=await fetch(`/api${path}`,{...options,headers});
  if(r.status===401&&retry&&accessToken){try{await refresh();return api(path,options,false);}catch(e){signedOut();throw e;}}
  if(!r.ok){let error=`Ошибка запроса (${r.status})`;try{error=(await r.json()).error||error;}catch{}throw new Error(error);}
  return r.status===204?null:r.json();
}
function signedOut(){accessToken=null;currentUser=null;rows=[];seenNotifications.clear();inboxLoaded=false;$('workspace').hidden=true;$('account').hidden=true;$('auth').hidden=false;}
async function signedIn(){
  $('email').textContent=currentUser.email;$('auth').hidden=true;$('workspace').hidden=false;$('account').hidden=false;
  $('auth-password').value='';$('status').textContent='';await loadTasks();await loadInbox();
}
$('auth-toggle').onclick=()=>{registering=!registering;$('auth-title').textContent=registering?'Регистрация':'Вход';$('auth-submit').textContent=registering?'Создать аккаунт':'Войти';$('auth-toggle').textContent=registering?'Уже есть аккаунт':'Создать аккаунт';$('auth-password').autocomplete=registering?'new-password':'current-password';$('auth-error').textContent='';};
$('auth-form').onsubmit=async e=>{
  e.preventDefault();$('auth-error').textContent='';$('auth-submit').disabled=true;
  const body=JSON.stringify({email:$('auth-email').value,password:$('auth-password').value});
  try{if(registering)await api('/auth/register',{method:'POST',body},false);const result=await api('/auth/login',{method:'POST',body},false);accessToken=result.accessToken;currentUser=result.user;await signedIn();}
  catch(error){$('auth-error').textContent=error.message;}finally{$('auth-submit').disabled=false;}
};
$('logout').onclick=async()=>{try{await api('/auth/logout',{method:'POST'},false);}finally{signedOut();}};
async function loadTasks(){
  rows=await api(`/tasks?${new URLSearchParams({state:$('state').value,sort:$('sort').value,search:$('search').value})}`);
  $('tasks').replaceChildren();$('summary').textContent=`Задач в списке: ${rows.length}`;
  if(!rows.length)$('tasks').append(element('p','Здесь пока нет задач. Создайте новую или измените фильтр.','empty'));
  for(const task of rows){
    const card=element('article','',`task ${task.completed?'done':''}`);card.append(element('h3',task.title));
    const late=!task.completed&&new Date(task.dueAt)<new Date();card.append(element('div',`${new Date(task.dueAt).toLocaleString('ru-RU')}${late?' · срок истёк':''}`,'meta'+(late?' late':'')));
    if(task.description)card.append(element('p',task.description));
    if(!task.isOwner)card.append(element('p',`Общая задача · ${task.ownerEmail} · ${task.canEdit?'редактирование':'только чтение'}`,'meta'));
    const actions=element('div','','actions');
    if(task.canEdit){actions.append(button(task.completed?'Вернуть в активные':'Готово',async()=>{await api(`/tasks/${task.id}/completion`,{method:'PATCH',body:JSON.stringify({completed:!task.completed})});await loadTasks();}));actions.append(button('Изменить',()=>openEditor(task)));}
    if(task.isOwner){actions.append(button('Доступ',()=>openShares(task.id)));actions.append(button('Удалить',async()=>{if(confirm(`Удалить задачу «${task.title}»?`)){await api(`/tasks/${task.id}`,{method:'DELETE'});await loadTasks();}},'danger'));}
    card.append(actions);$('tasks').append(card);
  }
}
for(const id of ['state','sort'])$(id).onchange=()=>loadTasks().catch(e=>$('status').textContent=e.message);
let searchTimer;$('search').oninput=()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>loadTasks().catch(e=>$('status').textContent=e.message),250);};
function localTime(value){const d=new Date(value);return new Date(d-d.getTimezoneOffset()*60000).toISOString().slice(0,16);}
function openEditor(task=null){editing=task?.id??null;$('editor-title').textContent=task?'Изменение задачи':'Новая задача';$('title').value=task?.title??'';$('description').value=task?.description??'';$('due').value=localTime(task?.dueAt??Date.now()+3600000);$('task-error').textContent='';$('editor').showModal();$('title').focus();}
$('new-task').onclick=()=>openEditor();$('cancel-edit').onclick=()=>$('editor').close();
$('task-form').onsubmit=async e=>{e.preventDefault();$('task-error').textContent='';const submit=e.target.querySelector('button');submit.disabled=true;
  try{await api(editing?`/tasks/${editing}`:'/tasks',{method:editing?'PUT':'POST',body:JSON.stringify({title:$('title').value,description:$('description').value,dueAt:new Date($('due').value).toISOString()})});$('editor').close();await loadTasks();await loadInbox();}
  catch(error){$('task-error').textContent=error.message;}finally{submit.disabled=false;}};
async function openShares(id){sharing=id;$('share-error').textContent='';$('sharing').showModal();await renderShares();}
async function renderShares(){const items=await api(`/tasks/${sharing}/shares`);$('shares').replaceChildren();for(const s of items){const row=element('div','','notice');row.append(element('p',`${s.email}: ${s.canEdit?'редактирование':'чтение'}`));row.append(button('Отозвать доступ',async()=>{await api(`/tasks/${sharing}/shares/${s.userId}`,{method:'DELETE'});await renderShares();},'danger'));$('shares').append(row);}}
$('close-share').onclick=()=>$('sharing').close();$('share-form').onsubmit=async e=>{e.preventDefault();try{await api(`/tasks/${sharing}/shares`,{method:'POST',body:JSON.stringify({email:$('share-email').value,canEdit:$('share-edit').checked})});$('share-email').value='';await renderShares();}catch(error){$('share-error').textContent=error.message;}};
$('enable-notifications').onclick=async()=>{if(!('Notification'in window)){$('status').textContent='Браузер не поддерживает уведомления. Напоминания остаются в списке.';return;}const permission=await Notification.requestPermission();$('status').textContent=permission==='granted'?'Уведомления включены':'Разрешение не выдано. Напоминания остаются в списке.';};
async function loadInbox(){const items=await api('/notifications');$('notifications').replaceChildren();if(!items.length)$('notifications').append(element('p','Напоминаний пока нет.','notice'));for(const n of items){if(inboxLoaded&&!seenNotifications.has(n.id)&&!n.isRead&&'Notification'in window&&Notification.permission==='granted')new Notification('Планировщик задач',{body:n.message,tag:`planner-${n.id}`});seenNotifications.add(n.id);const row=element('div','',`notice ${n.isRead?'read':''}`);row.append(element('p',n.message));if(!n.isRead)row.append(button('Прочитано',async()=>{await api(`/notifications/${n.id}/read`,{method:'PATCH'});await loadInbox();}));$('notifications').append(row);}inboxLoaded=true;}
setInterval(async()=>{if(!accessToken||polling||document.hidden)return;polling=true;try{await loadInbox();}catch(e){$('status').textContent=e.message;}finally{polling=false;}},5000);
refresh().then(signedIn).catch(()=>signedOut());

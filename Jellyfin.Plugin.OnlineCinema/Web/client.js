(function(){
'use strict';
if(window.__onlineCinemaV012)return;window.__onlineCinemaV011=true;
const state={type:'movie',page:1,total:1,query:'',busy:false};
const id='oc-user-view';
const api=path=>{const client=window.ApiClient;if(!client)throw Error('Jellyfin API is not ready');return client.getJSON(client.getUrl('OnlineCinema/'+path));};
const el=(tag,cls,txt)=>{const node=document.createElement(tag);if(cls)node.className=cls;if(txt!==undefined)node.textContent=txt;return node;};
function menu(){
 const existing=document.getElementById('oc-user-link');
 if(existing && existing.isConnected)return;
 // Jellyfin 12 uses a React/MUI drawer, not the old userMenuOptions container.
 const target=document.querySelector('.MuiDrawer-paper ul[aria-labelledby="libraries-subheader"]')
 || document.querySelector('.MuiDrawer-paper ul')
 || document.querySelector('.mainDrawer-scrollContainer .userMenuOptions,.mainDrawer-scrollContainer');
 if(!target)return;
 const wrapper=el('li','oc-menu-item');
 wrapper.id='oc-user-menu-item';
 const a=el('a','oc-menu-link','🎬  Онлайн-кинотеатр');
 a.id='oc-user-link';a.href='#/online-cinema';
 a.addEventListener('click',event=>{event.preventDefault();open();});
 wrapper.append(a);target.append(wrapper);
}
function homeEntry(){
 // Fallback for users who do not open the drawer; keep the catalog in the user UI.
 const target=document.querySelector('#homeTab .sections');
 if(!target || target.querySelector('#oc-home-entry'))return;
 const button=el('button','oc-home-entry','🎬  Онлайн-кинотеатр');
 button.id='oc-home-entry';button.onclick=open;
 target.prepend(button);
}

function open(){
 location.hash='#/online-cinema';
 let view=document.getElementById(id);
 if(!view){view=el('section','oc-user-view');view.id=id;document.body.append(view);}
 view.hidden=false;document.body.classList.add('oc-cinema-open');
 draw(view);load();
}
function close(){const v=document.getElementById(id);if(v)v.hidden=true;document.body.classList.remove('oc-cinema-open');}
function draw(v){
 v.replaceChildren();const container=el('div','oc-container');
 const top=el('div','oc-top');
 const back=el('button','','← Jellyfin');back.onclick=()=>{close();location.hash='#/home';};
 top.append(back,el('h1','','Онлайн-кинотеатр'));container.append(top);
 const search=el('form','oc-search');const input=el('input');input.type='search';input.placeholder='Название фильма или сериала';input.value=state.query;
 const submit=el('button','','Найти');submit.type='submit';search.append(input,submit);
 search.onsubmit=e=>{e.preventDefault();state.query=input.value.trim();state.page=1;load();};
 container.append(search);
 const tabs=el('div','oc-tabs');
 [['movie','Фильмы'],['tv','Сериалы']].forEach(([key,label])=>{const b=el('button',state.type===key?'oc-active':'',label);b.onclick=()=>{state.type=key;state.query='';state.page=1;draw(v);load();};tabs.append(b);});
 const select=el('select');[['popular','Популярное'],['top_rated','Высокий рейтинг'],['now_playing','Сейчас в кино / в эфире']].forEach(([key,label])=>{const opt=el('option','',label);opt.value=key;select.append(opt);});
 select.onchange=()=>{state.page=1;load();};select.id='oc-user-category';tabs.append(select);container.append(tabs);
 const status=el('p');status.id='oc-user-status';container.append(status);
 const grid=el('div','oc-grid');grid.id='oc-user-grid';container.append(grid);
 const pages=el('div','oc-pages');pages.id='oc-user-pages';container.append(pages);
 const details=el('div','oc-details');details.id='oc-user-details';details.hidden=true;container.append(details);v.append(container);
}
async function load(){
 if(state.busy)return;
 const v=document.getElementById(id);if(!v||v.hidden)return;
 const grid=v.querySelector('#oc-user-grid'),status=v.querySelector('#oc-user-status'),pages=v.querySelector('#oc-user-pages'),details=v.querySelector('#oc-user-details');
 details.hidden=true;grid.hidden=false;pages.hidden=false;
 state.busy=true;status.textContent='Загрузка...';
 try{
  const path=state.query?'search?query='+encodeURIComponent(state.query)+'&type=multi&page='+state.page:
  'discover?type='+state.type+'&category='+v.querySelector('#oc-user-category').value+'&page='+state.page;
  const result=await api(path);grid.replaceChildren();state.total=Math.max(1,result.totalPages||1);
  (result.items||[]).forEach(item=>{
   const card=el('button','oc-card');const img=el('img','oc-poster');img.loading='lazy';img.alt=item.title||'';if(item.posterUrl)img.src=item.posterUrl;
   card.append(img,el('div','oc-title',item.title+' ('+(item.year||'—')+')'),
    el('div',item.localItemId?'oc-badge oc-local':'oc-badge',item.localItemId?'В библиотеке':'Онлайн'));
   card.onclick=()=>detailsPage(item);grid.append(card);
  });
  pages.replaceChildren();
  const prev=el('button','','Назад');prev.disabled=state.page===1;prev.onclick=()=>{state.page--;load();};
  const next=el('button','','Вперёд');next.disabled=state.page>=state.total;next.onclick=()=>{state.page++;load();};
  pages.append(prev,el('span','',state.page+' / '+state.total),next);
  status.textContent=grid.childElementCount?'':'Ничего не найдено';
 }catch(e){status.textContent='Ошибка каталога: '+(e.message||'проверь настройки TMDB в панели управления');}
 finally{state.busy=false;}
}
async function detailsPage(item){
 const v=document.getElementById(id),grid=v.querySelector('#oc-user-grid'),pages=v.querySelector('#oc-user-pages'),details=v.querySelector('#oc-user-details'),status=v.querySelector('#oc-user-status');
 grid.hidden=true;pages.hidden=true;details.hidden=false;details.replaceChildren();status.textContent='Загрузка карточки...';
 const back=el('button','','← Назад');back.onclick=()=>load();details.append(back);
 try{
  const r=await api('details/'+item.type+'/'+item.id),info=r.item;
  const block=el('div','oc-info');const poster=el('img','oc-details-poster');if(info.posterUrl)poster.src=info.posterUrl;poster.alt=info.title;
  const content=el('div');content.append(el('h2','',info.title),el('p','',info.overview||'Нет описания'),
      el('p','',info.localItemId?'В библиотеке':'Онлайн'));block.append(poster,content);details.append(block);
  if(info.localItemId){const b=el('button','','Открыть в библиотеке');b.onclick=()=>{close();location.hash='#!/details?id='+encodeURIComponent(info.localItemId);};content.append(b);}
  const notice=el('p','','Выбор торрент-раздач и воспроизведение появятся в следующей версии.');content.append(notice);
  (r.seasons||[]).forEach(season=>{const b=el('button','oc-season',season.name||'Сезон '+season.number);b.onclick=async()=>{
   status.textContent='Загрузка серий...';
   try{const s=await api('seasons/'+item.id+'/'+season.number);details.querySelectorAll('.oc-episode').forEach(n=>n.remove());
    (s.episodes||[]).forEach(ep=>details.append(el('div','oc-episode','Серия '+ep.number+': '+ep.name)));status.textContent='';}
   catch(e){status.textContent='Не удалось получить серии: '+e.message;}
  };details.append(b);});
  status.textContent='';
 }catch(e){status.textContent='Не удалось загрузить карточку: '+e.message;}
}
const observer=new MutationObserver(()=>{menu();homeEntry();const v=document.getElementById(id);if(v&&!v.hidden&&!location.hash.startsWith('#/online-cinema'))close();});
function init(){if(!document.body)return;observer.observe(document.body,{childList:true,subtree:true});menu();homeEntry();if(location.hash.startsWith('#/online-cinema'))open();}
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',init,{once:true});else init();
window.addEventListener('hashchange',()=>{if(location.hash.startsWith('#/online-cinema')){const v=document.getElementById(id);if(!v||v.hidden)open();}else close();});
})();

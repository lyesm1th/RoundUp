/* RoundUp 6.3.1 — reversible project trash; original GeoZamir data stays untouched. */
(() => {
  "use strict";
  const bridge=window.RoundUpBridge;
  const options=document.getElementById("projectOptions");
  const menu=document.getElementById("projectMenu");
  if(!bridge||!options||!menu)return;
  let deleted=new Set(),loaded=false,busy=false;
  const endpoint="/roundup/project-trash";
  const readList=()=>bridge.state.projects?.projects||[];
  const isDeleted=id=>deleted.has(String(id));
  const feedback=message=>window.alert(message);
  const save=async next=>{
    const body=JSON.stringify({version:1,deleted:[...next]});
    const response=await fetch(endpoint,{method:"PUT",headers:{"Content-Type":"application/json"},body});
    if(!response.ok)throw Error("HTTP "+response.status);
    deleted=next;
    bridge.refreshProjects?.();
    paintTrashButton();
  };
  const trashButton=document.createElement("button");
  trashButton.type="button";trashButton.id="projectTrashBtn";trashButton.className="ru-project-trash-entry";
  options.insertAdjacentElement("afterend",trashButton);
  const paintTrashButton=()=>{
    trashButton.textContent="♻ Кошик проєктів"+(deleted.size?" ("+deleted.size+")":"");
  };
  const make=(tag,cls,txt)=>{
    const el=document.createElement(tag);
    if(cls)el.className=cls;
    if(txt!==undefined)el.textContent=txt;
    return el;
  };
  function showTrash(){
    menu.hidden=true;
    const shade=make("div","ru-profile-modal-backdrop");
    const panel=make("section","ru-profile-modal ru-trash-modal");
    panel.setAttribute("role","dialog");
    panel.setAttribute("aria-modal","true");
    panel.append(make("h2","","Кошик проєктів"));
    panel.append(make("p","","Проєкти приховані зі списку, але їхні файли, фото та архіви залишаються на диску. Їх можна відновити."));
    const list=make("div","ru-trash-list");
    const close=make("button","ru-small-btn","Закрити");close.type="button";
    const dismiss=()=>{document.removeEventListener("keydown",keyHandler);shade.remove();};
    const keyHandler=ev=>{if(ev.key==="Escape")dismiss();};
    close.addEventListener("click",dismiss);
    const actions=make("div","ru-modal-actions");actions.append(close);
    panel.append(list,actions);shade.append(panel);document.body.append(shade);
    shade.addEventListener("click",event=>{if(event.target===shade)dismiss();});
    document.addEventListener("keydown",keyHandler);
    const render=()=>{
      list.replaceChildren();
      const items=readList().filter(p=>deleted.has(String(p.id)));
      if(!items.length){list.append(make("p","ru-empty-trash","Кошик порожній"));return;}
      for(const p of items){
        const row=make("div","ru-trash-project-row");
        row.append(make("strong","",p.name||"Без назви"));
        const restore=make("button","ru-small-btn","Відновити");restore.type="button";
        restore.addEventListener("click",async()=>{
          if(busy)return;busy=true;restore.disabled=true;
          try {
            const next=new Set(deleted);next.delete(String(p.id));
            await save(next);render();
          }catch(error){feedback("Не вдалося відновити проєкт: "+error.message);}
          finally{busy=false;restore.disabled=false;}
        });
        row.append(restore);list.append(row);
      }
    };
    render();close.focus();
  }
  async function deleteProject(id){
    if(!loaded){feedback("Зачекайте, завантажується список проєктів.");return;}
    if(busy)return;
    id=String(id);
    const item=readList().find(p=>String(p.id)===id);
    if(!item||isDeleted(id))return;
    const active=readList().filter(p=>!isDeleted(p.id));
    if(active.length<=1){feedback("Не можна видалити останній проєкт. Спочатку створіть інший.");return;}
    if(!confirm("Перемістити проєкт «"+item.name+"» у кошик?\n\nЙого точки, архіви та фото не стиратимуться. Проєкт можна відновити через «Кошик проєктів»."))return;
    busy=true;
    try {
      if(bridge.state.currentProjectId===id){
        const other=active.find(p=>String(p.id)!==id);
        if(!other)throw Error("Немає іншого проєкту для переходу.");
        await bridge.switchProject?.(other.id);
        if(bridge.state.currentProjectId===id)throw Error("Не вдалося відкрити інший проєкт.");
      }
      const next=new Set(deleted);next.add(id);
      await save(next);
    }catch(error){feedback("Не вдалося перемістити проєкт у кошик: "+error.message);}
    finally{busy=false;}
  }
  trashButton.addEventListener("click",()=>{if(!loaded){feedback("Список проєктів ще завантажується.");return;}showTrash();});
  window.RoundUpProjectTrash={isDeleted,deleteProject,openTrash:showTrash};
  paintTrashButton();
  (async()=>{
    try{
      const response=await fetch(endpoint,{cache:"no-store"});
      if(!response.ok)throw Error("HTTP "+response.status);
      const obj=await response.json();
      if(!Array.isArray(obj.deleted))throw Error("Невірний формат кошика");
      deleted=new Set(obj.deleted.map(String).filter(s=>s.length>0&&s.length<200));
      loaded=true;
    }catch(error){
      console.error("RoundUp project trash unavailable:",error);
      trashButton.textContent="⚠ Кошик недоступний";
      return;
    }
    paintTrashButton();bridge.refreshProjects?.();
  })();
})();
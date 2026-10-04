'use strict';
// Standards-compatible DOM convenience method missing in Chromium 80-85.
// Local only; never download polyfills into the privileged terminal origin.
[Element.prototype, Document.prototype, DocumentFragment.prototype].forEach(function (prototype) {
  if (prototype.replaceChildren) return;
  Object.defineProperty(prototype, 'replaceChildren', {configurable:true, writable:true, value:function () {
    var fragment = document.createDocumentFragment();
    for (var i=0; i<arguments.length; i++) fragment.appendChild(
      arguments[i] instanceof Node ? arguments[i] : document.createTextNode(String(arguments[i])));
    while (this.firstChild) this.removeChild(this.firstChild);
    this.appendChild(fragment);
  }});
});

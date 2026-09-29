// The portal's only script. Plain JavaScript, served from the portal itself,
// with no inline handlers, so the content security policy can forbid inline
// code outright. Sign-in and sign-out go to the API's own session endpoint,
// which keeps the throttle, the lockout and the cookie in one place.
(function () {
  "use strict";

  function antiForgeryToken() {
    var meta = document.querySelector('meta[name="prem-antiforgery"]');
    return meta ? meta.getAttribute("content") : "";
  }

  var signIn = document.querySelector("form[data-sign-in]");
  if (signIn) {
    signIn.addEventListener("submit", function (event) {
      event.preventDefault();
      var message = document.querySelector("[data-sign-in-message]");
      message.textContent = "";
      fetch("/api/session", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          signInName: signIn.elements.namedItem("signInName").value,
          password: signIn.elements.namedItem("password").value
        })
      }).then(function (response) {
        if (response.ok) {
          window.location.assign(signIn.getAttribute("data-return") || "/portal");
          return;
        }
        if (response.status === 429) {
          message.textContent = "Too many attempts from this address. Wait a few minutes and try again.";
        } else if (response.status === 403) {
          message.textContent = "Sign-in needs HTTPS on this server.";
        } else {
          message.textContent = "Sign-in failed.";
        }
      }).catch(function () {
        message.textContent = "The server did not answer. It may be restarting; try again.";
      });
    });
  }

  // Copy buttons, for the configuration and the token shown once on the
  // connect page. The clipboard needs a secure page; where there is none the
  // text is selected instead, so the person copies it with the keyboard.
  Array.prototype.forEach.call(document.querySelectorAll("[data-copy]"), function (button) {
    button.addEventListener("click", function () {
      var target = document.getElementById(button.getAttribute("data-copy"));
      if (!target) return;
      var select = function () {
        var range = document.createRange();
        range.selectNodeContents(target);
        var selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
        button.textContent = "Selected: press Ctrl+C";
      };
      if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(target.textContent).then(function () {
          button.textContent = "Copied";
        }, select);
      } else {
        select();
      }
    });
  });

  // The consent page's two forms, Approve and Deny: once either is sent,
  // neither button can be pressed again. Each form carries its decision as a
  // hidden field of its own, so a disabled button drops nothing from it.
  Array.prototype.forEach.call(document.querySelectorAll("form[data-consent]"), function (form) {
    form.addEventListener("submit", function () {
      Array.prototype.forEach.call(document.querySelectorAll("form[data-consent] button"), function (button) {
        button.disabled = true;
      });
    });
  });

  // After Approve or Deny: the page holds one link back to the assistant,
  // followed at once. The server answers with that link rather than a
  // redirect, which the policy's form-action would stop after the post.
  var onward = document.querySelector("[data-continue]");
  if (onward) {
    window.location.replace(onward.getAttribute("data-continue"));
  }

  var signOut = document.querySelector("[data-sign-out]");
  if (signOut) {
    signOut.addEventListener("click", function () {
      fetch("/api/session", {
        method: "DELETE",
        credentials: "same-origin",
        headers: { "X-Prem-Antiforgery": antiForgeryToken() }
      }).finally(function () {
        window.location.assign("/portal/sign-in");
      });
    });
  }
})();

# Changes Arcana makes to the engine

Arcana builds Obscura from a pinned source commit (`engine.lock.json`). Where Arcana needs something the
engine does not do yet, the change is a patch in `engine-patches/`, applied in order by `tools/fetch-engine.ps1`
after the checkout and before the build, each checked against the digest in the lock. Because a patched build
has nothing to download, `-UseRelease` is refused while patches are listed.

## 0001: child frames work for CDP clients

A client could list a child frame but not look into it. Playwright and Puppeteer need three things the engine did
not give:

- an execution context for each child frame (announced when the frame appears, together with the client's
  isolated worlds);
- `Runtime` and `DOM` commands that run in the frame that holds the object, or whose context, they name (object
  ids made in a frame carry that frame in `injectedScriptId`);
- `DOM.describeNode` reporting `frameId` on `<html>` and on `<iframe>`/`<frame>`, which is how a client learns
  which frame owns a document and which frame an iframe holds.

With these, a snapshot expands iframes, `srcdoc` iframes and nested iframes, `frame.evaluate` and locators work in
a frame, and `DOM.setFileInputFiles` finds a file input in whatever frame holds it. A frameset document becomes a
body of iframes so its frames load.

## 0002: multipart forms send the chosen files

A form submit always urlencoded its fields and skipped file inputs. `enctype="multipart/form-data"` now sends each
field as a part and each chosen file as its bytes. The page hands the network layer a body that starts with a
marker line naming the content type and carries the rest as a binary string (one character per byte); the network
layer sends the rest as bytes.

## 0003: frames of a new document are announced again

The engine remembers which child frames it has announced to a client. A new document's frames reuse the old ids,
so they counted as already announced, and the second page with frames in a session read as empty. A committed
document now forgets what was announced.

## 0004: a frame follows its own links

A link clicked in a child frame, or the frame's `location.href`, was recorded against the frame and never acted on:
the frame's location changed and its document stayed. The page now has the frame's element load the address, as
the first load was done. The page's own address check also read the frame's address while a command was aimed at
a frame, which moved the top page's URL; it now reads the page's.

## Moving the pin

The weekly pin bump changes `sourceCommit`. If a patch no longer applies, the build fails at that patch: rebase
it on the new source (`git format-patch` from a checkout of the new commit) and update the digests in the lock.

// callback methods from RequestService class
function renderQRCode(url) {
    document.getElementById('qrcode').style.display = "block";
    document.getElementById("qrcode").getElementsByTagName("img")[0].style.opacity = "1.0";
    qrcode.makeCode(url);
}
function dimQRCode() {
    document.getElementById("qrcode").getElementsByTagName("img")[0].style.opacity = "0.1";
}
function hideQRCode() {
    document.getElementById("qrcode").style.display = "none";
    document.getElementById("qrcode").getElementsByTagName("img")[0].style.display = "none";
    var pinCode = document.getElementById('pinCode');
    if ( null != pinCode ) {
        pinCode.style.display = "none";
    }
}
function displayMessage(msg) {
    document.getElementById("message-wrapper").style.display = "block";    
    document.getElementById('message').innerHTML = msg;
}
function drawQRCode(requestType, id, url, pinCode) {
    renderQRCode(url);
    if (requestType == "presentation") {
        document.getElementById('verify-credential').style.display = "none";
        displayMessage("Waiting for QR code to be scanned");
    } else if (requestType == "issuance") {
        document.getElementById('issue-credential').style.display = "none";
        document.getElementById('take-selfie').style.display = "none";
        displayMessage("Waiting for QR code to be scanned");
        if ( pinCode != undefined ) {
            document.getElementById('pinCode').innerHTML = "Pin code: " + pinCode;
            document.getElementById('pinCode').style.display = "block";
        }
    } else if (requestType == "selfie") {
        displayMessage("Waiting for QR code to be scanned with QR code reader app");
    }
}
function navigateToDeepLink(requestType, id, url) {
    document.getElementById('verify-credential').style.display = "none";
    document.getElementById('check-result').style.display = "block";
    window.location.href = url;
}
function requestRetrieved(requestType) {
    dimQRCode();
    if (requestType == "presentation") {
        displayMessage("QR code scanned. Waiting for Verified ID credential to be shared from wallet...");
    } else {
        displayMessage("QR code scanned. Waiting for Verified ID credential to be added to wallet...");
    }
}
function presentationVerified(id, response) {
    hideQRCode();
    displayMessage("Presentation verified: <br/><br/>" + JSON.stringify(response.claims));
    window.location = 'PresentationVerified?id=' + id;
}
function issuanceComplete(id, response) {
    hideQRCode();
    displayMessage("Issuance completed");
}
function selfieTaken(id, response) {
    hideQRCode();
    displayMessage("Selfie taken");
    document.getElementById('selfie').src = "data:image/png;base64," + response.photo;
    document.getElementById('selfie').style.display = "block";
}
function requestError(requestType, response) {
    hideQRCode();
    console.log(JSON.stringify(response));
    displayMessage(`${requestType} error: ` + JSON.stringify(response));
}
// method to post selfie taken before starting an issuance request
function setUserPhoto() {
    if ("none" != document.getElementById('selfie').style.display && document.getElementById('selfie').src != "") {
        photoId = requestService.setUserPhoto(document.getElementById('selfie').src);
    }
}
// RequestService object that drives the interaction with backend APIs
// verifiedid.requestservice.client.js
var requestService = new RequestService(drawQRCode,
    navigateToDeepLink,
    requestRetrieved,
    presentationVerified,
    issuanceComplete,
    selfieTaken,
    requestError
);
// If to do console.log (a lot)
requestService.logEnabled = true;


class Marker:
    def __init__(self, pattern, weight):
        self.pattern = pattern
        self.weight = weight
class DocType:
    def __init__(self, name, markers):
        self.name = name
        self.markers = markers
class Score:
    def __init__(self, doc_type_name, value):
        self.doc_type_name = doc_type_name
        self.value = value

invoice_markers = [
    Marker("bill to ", 2.0),
    Marker("invoice no", 3.0),
    Marker("subtotal", 2.0),
    Marker("due date", 1.0),
    Marker("tax", 1.0)
]
receipt_markers = [
    Marker("receipt", 2.0),
    Marker("total amount", 2.0),
    Marker("cash", 1.0),
    Marker("change", 1.0),
    Marker("thank you", 1.0)
]
contract_markers = [
    Marker("whereas", 3.0),
    Marker("parties", 1.0),
    Marker("in witness whereof", 3.0),
    Marker("party a", 2.0),
    Marker("party b", 2.0),
    Marker("scope of work", 2.0),
    Marker("compensation", 1.0),
    Marker("confidentiality", 1.0)
]

doc_types = [
    DocType("invoice", invoice_markers),
    DocType("receipt", receipt_markers),
    DocType("contract", contract_markers),
]

def keyword_scan(text, doc_types):
text = text.lower()
scores = []
for doc_type in doc_types:
    total = 0
    for marker in doc_type.markers:
        if marker.pattern in text:
            total = total + marker.weight
    scores.append(Score(doc_type.name, total))
return scores


def verdict(scores):
for i in range(len(scores)):
    for j in range(i + 1, len(scores)):
        if abs(scores[i].value - scores[j].value) <= 2:
            return "needs-review"

best = scores[0]
for score in scores:
    if score.value > best.value:
        best = score
return best.doc_type_name